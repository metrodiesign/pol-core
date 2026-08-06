# โมดูล Products

> เอกสารรวมทุกอย่างเกี่ยวกับโมดูล Products ไว้ที่เดียว — เดิมเนื้อหากระจายอยู่ใน
> `entity-fields.md` / `platform-modules.md` / `src-structure.md` / `layers-guide.md` /
> `db-connection-and-rls.md` / `search-filter-sort.md`; ไฟล์เหล่านั้นตอนนี้ชี้กลับมาที่นี่แทน
> การพูดซ้ำ — แก้ข้อมูล Products ที่นี่ที่เดียว

> **เปลี่ยนสถาปัตยกรรมทั้งยวง 2026-08-06** (spec `products-external-source-of-truth`, PR/commit
> `27af452`): เดิมโมดูลนี้ค้นสดจากต้นทางแล้ว **upsert สำเนากลับเข้าตาราง `shop.Products`**
> เพื่อมินต์ `Guid` ให้แต่ละเอกสาร (สถาปัตยกรรมนั้นมาจาก spec `products-sp-gateway` +
> `products-sp-53-alignment`) — งานล่าสุดนี้ **ลบตาราง `shop.Products` ทิ้งทั้งตาราง** แล้วเปลี่ยนทุก
> จุดให้อ่านสดล้วน ไม่มีสำเนาในระบบเราอีกต่อไป เอกสารด้านล่างทั้งหมดอธิบายสภาพปัจจุบัน (อ่านสดล้วน)

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
ง่าย ๆ เหมือน **ล่าม** ที่ยืนอยู่หน้าประตูทุกครั้งที่มีคนถาม ไม่ใช่เจ้าหน้าที่รับเอกสารเข้าคลัง
อีกต่อไป (เดิมเคย "รับเข้า" มาเก็บสำเนาไว้ ตอนนี้ไม่เก็บอะไรเลย):

1. **แปลคำถามของเราเป็นภาษาที่เขาเข้าใจ** — ผู้ใช้ต้องระบุก่อนว่าจะค้นฝั่งไหน (ประกันรถ หรือ
   ไม่ใช่รถ) หรือไม่ก็ระบุ `productGroup` ที่บอกฝั่งอยู่ในตัว (เช่น `CMI` = รถ) เพราะช่องบริการ
   ทั้ง 2 ฝั่งแยกกันเด็ดขาด รวมกันไม่ได้ — ถ้าไม่ระบุเลยทั้งคู่ ระบบจะปฏิเสธคำถามทันทีแทนที่จะ
   เดาให้ ส่วน **รหัสผู้ขาย** (`saleCode`) ไม่ใช่ค่าที่ผู้ใช้ระบุอีกต่อไป — ระบบดึงจากบัญชี
   merchant user ที่ล็อกอินอยู่เองเสมอ
2. **รับคำตอบตามที่เขาส่งมา โดยไม่รีบเชื่อ** — คำตอบที่ได้กลับมาถือเป็น "ข้อมูลดิบ" ยังไม่ใช่
   ข้อมูลที่ระบบเราจะใช้งานตรง ๆ (เหตุผลอยู่หัวข้อถัดไป)
3. **กรองก่อนแสดง โดยไม่เก็บอะไรไว้เลย** — ถ้าคำตอบผิดรูปแบบ หรือข้อมูลบางแถวไม่ครบ (เช่น
   ไม่มีเลขกรมธรรม์) แถวนั้นจะถูก**ข้าม**ไม่แสดงผล พร้อมบันทึกเหตุผลไว้ให้ตรวจสอบย้อนหลังได้
   ไม่ทำให้หน้าจอทั้งหน้าใช้งานไม่ได้เพราะข้อมูลแค่แถวเดียวเสีย — แต่ต่างจากเดิมตรงที่**ไม่มีการ
   บันทึกแถวที่ผ่านการกรองแล้วลงฐานข้อมูลของเราอีกเลย** ทุกครั้งที่ค้นคือการยิงไปถามต้นทางใหม่
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

อีกเหตุผลคือ **ป้องกันการขายซ้ำ** — แต่วิธีทำต่างจากเดิมโดยสิ้นเชิง เดิมเราเคยจดสถานะ "จ่ายแล้ว"
ไว้ในสำเนาของเราเอง ตอนนี้ต้นทางเป็น **read-only ล้วน ไม่มีวันรู้ว่าเราขายเอกสารไปหรือยัง** ระบบ
จึงต้อง**อนุมาน**จากประวัติ order ของเราเองแทน: เอกสารใบไหนมี order ที่จ่ายเงินสำเร็จแล้วถืออยู่
ใบนั้นขายไม่ได้อีก แม้ต้นทางจะยังรายงานว่ายังไม่จ่ายก็ตาม (ดูหัวข้อ "กันขายเอกสารซ้ำ" ด้านล่าง)

## สรุปเป็นภาพเดียว

```
ผู้ใช้ค้นหากรมธรรม์ (ระบุฝั่งรถ/ไม่ใช่รถ หรือ productGroup ที่บอกฝั่งอยู่ในตัว)
        │
        ▼
   [จุดเชื่อมต่อนี้]  ── ยิงไปช่องบริการฝั่งที่ผู้ใช้ระบุ ด้วยรหัสผู้ขายของผู้ใช้เอง
        │
        ▼
  ระบบต้นทาง (hippo หรือ mammoth) ตอบข้อมูลดิบกลับมา
        │
        ▼
   [จุดเชื่อมต่อนี้]  ── กรอง/แปลงเป็นรูปแบบของเรา, ทิ้งแถวที่ข้อมูลไม่ครบ (ไม่บันทึกที่ใด)
        │
        ▼
   ถามประวัติ order ของเราเอง ── ใบไหนมี order จ่ายแล้วถืออยู่ = ขายไปแล้ว ตัดออก/แจ้งเตือน
        │
        ▼
      ผู้ใช้เห็นผลลัพธ์
```

หมายเหตุ: วันนี้ "ระบบต้นทาง" ที่เราเชื่อมอยู่เป็น**ระบบจำลอง**ที่สร้างขึ้นเองภายในบริษัท
(ให้หน้าตา/พฤติกรรมเหมือนของจริงทุกประการ) เพื่อให้ทีมอื่นพัฒนาต่อได้โดยไม่ต้องรอเชื่อมกับ
ระบบจริงของอีก 2 หน่วยงานก่อน — วันที่เชื่อมของจริง ระบบจะไปติดต่อ "ที่อยู่" ของอีกฝั่งแทน แต่ทีม
เทคนิคต้องเตรียมเปิดทางเชื่อมต่อนั้นไว้ก่อน ไม่ใช่แค่เปลี่ยนค่าแล้วเสร็จทันที (ดูย่อหน้า
"สถานะ: มีแล้ว" ท้ายหัวข้อถัดไป)

---

## บริบท + บทบาท (technical)

สิ่งที่ขายบนแพลตฟอร์ม = **เอกสารประกัน** (กรมธรรม์/ใบคำขอ/สลักหลัง/ใบต่ออายุ) ที่รอเก็บเงิน
**ไม่มีตารางในฐานข้อมูลของเราเก็บมันอีกแล้ว** — โมดูลนี้เป็นแค่ชั้นแปลระหว่าง HTTP กับสอง
stored procedure ต้นทาง (`usp_Motor_SearchDocument` / `usp_NonMotor_SearchDocument` ตาม
[`vcentralpay-sp-quick-reference.pdf`](./vcentralpay-sp-quick-reference.pdf)) และ **ตัวระบุเอกสาร
คือ `DocumentNo` (string) โดยตรง** ไม่มีการมินต์ `Guid` ให้เอกสารอีกต่อไป

- ทุกชั้นของ purchase path (ตะกร้า, checkout, order, payment session) พก `DocumentNo` เป็น
  ตัวระบุเอกสารแทน `ProductId` เดิม — ดูหัวข้อ "ตัวระบุเอกสาร" ด้านล่าง
- **`GET /products` เป็น read-only แท้จริงตอนนี้** (ตรงข้ามกับเดิมที่เป็น read-then-write): ไม่มี
  `SaveChanges`, ไม่มี INSERT/UPDATE/DELETE ใด ๆ ในเส้นทางตอบคำขอ
- เป็น source ของเบี้ย**และ field เอกสาร**เสมอ — ทั้ง Cart (เบี้ย) และ Checkout (field เอกสาร)
  อ่านสดจากต้นทางทุกครั้งที่ต้องใช้ ไม่รับค่าเหล่านี้จาก client และไม่อ่านจากที่เก็บใด ๆ ของเรา
- endpoints: `GET /products` **ตัวเดียว** ยังคงเป็น endpoint เดียวของโมดูลนี้ — เอกสารมาจาก
  ระบบกรมธรรม์ต้นทาง ไม่ได้เกิดจาก merchant กรอกฟอร์ม และตอนนี้**ไม่มี write seam เหลืออยู่เลย**
  (`CreateProductCommand`/`GetProductByIdQuery` เดิมถูกลบทั้งคู่ พร้อมกับ aggregate ที่มันเขียนถึง)
- การอ่านเอกสารรายใบยังมีอยู่แต่**เปลี่ยนรูปทั้งหมด**: `LookupDocumentQuery` (internal, ไม่ map
  เป็น route) ยิง SP สดทุกครั้งที่ add-item/checkout ต้องการราคาหรือ snapshot ฟิลด์เอกสาร — ไม่มี
  การอ่านจากตารางท้องถิ่นอีกแล้วเพราะไม่มีตารางให้อ่าน
- **กันขายเอกสารใบเดิมซ้ำ** เปลี่ยนกลไกทั้งหมด: จากเดิมที่จดสถานะ `PAID` ไว้ในสำเนาของเราเอง
  กลายเป็น**อนุมานจากประวัติ Orders** ผ่านพอร์ต `IDocumentSaleProbe` (ดูหัวข้อถัดไป) — เพราะ
  ต้นทางเป็น read-only ล้วน ไม่มีวันรู้ว่าเราขายเอกสารไปหรือยัง

### ตารางฟีเจอร์

| ฟีเจอร์ | รายละเอียด | สถานะ |
|---|---|---|
| สร้างเอกสาร | ไม่มี HTTP endpoint และ**ไม่มี write seam ภายในเหลืออยู่เลย** — `CreateProductCommand` ถูกลบทั้งตัวพร้อม aggregate ที่มันเขียนถึง แคตตาล็อกเป็นสิ่งที่อ่านสดจากต้นทางเท่านั้น | ถอดออกแล้ว |
| เบี้ยเป็น source of truth | Cart เรียก `LookupDocumentQuery` สดทุกครั้งที่ add-item แล้ว mint `Money.Of(document.TotalPremium, "THB")` — ไม่รับเบี้ยจาก client และไม่มีสำเนาให้อ่านซ้ำ | มีแล้ว |
| field เอกสารบนเอกสารที่อ่านได้ | `DocumentNo`/`ProductGroup`/`DocumentType`/`PolicyNumber`/`StartDate`/`EndDate` — checkout อ่านสดอีกครั้งต่อบรรทัดตอนเริ่ม checkout แล้ว snapshot เข้า `OrderItem`/`CheckoutSessionItem` (server-side, ไม่รับจาก client; ราคายังคงใช้ค่าที่บันทึกไว้ตอน add-item ไม่ใช่ราคาที่อ่านสดใหม่) | มีแล้ว |
| List ตาม §2 input contract | `GET /products` — `page`/`limit` (cap 25) + typed `productFilters` ที่**ไม่บังคับส่งมาอีกต่อไป** (ไม่ส่งเลย = ทุก filter เป็นค่าเริ่มต้น) แต่ต้องมี `productGroup` หรือ `insuranceType` อย่างน้อยหนึ่งอย่างเสมอ (ไม่งั้น 400) — `saleCode` **ถูกตัดออกจาก `productFilters` ทั้ง member** เพราะเป็นค่าที่ server กำหนดจาก merchant user ที่ล็อกอินอยู่แล้ว (client ส่งมาจะถูกเมิน) | มีแล้ว (products-external-source-of-truth) |
| ค้นสดจากต้นทาง — ไม่มีสำเนาอีกแล้ว | `GET /products` เรียก `ISpDocumentGateway.SearchAsync` -> map แถว wire เป็น `DocumentView` (ข้ามแถวที่ key ว่าง/เบี้ยไม่ถูก/enum ไม่รู้จัก/ซ้ำกันในหน้าเดียว + log warning) -> ตอบกลับตรง ๆ **ไม่มีขั้นตอน upsert เข้าฐานข้อมูลของเราอีกต่อไป** — ตาราง `shop.Products` ถูก DROP ทิ้งทั้งตาราง | มีแล้ว (products-external-source-of-truth) |
| กันขายเอกสารใบเดิมซ้ำ | อนุมานจาก `Orders`/`PaymentSessions` ผ่าน `IDocumentSaleProbe` แทนสถานะที่เคยเก็บไว้เอง — ดูหัวข้อ "กันขายเอกสารซ้ำ" ด้านล่าง | มีแล้ว (products-external-source-of-truth) |
| Response envelope §5.1 | `ProductPage` = `items` + `totalRows?`/`totalPages?`/`pageNo`/`pageSize`/`hasNextPage`/`hasPreviousPage`/`countMode`/`searchWindowMonths` คัดลอกจาก result set แรกของ SP ตรง ๆ; แต่ละ item **ไม่มีฟิลด์ `id`** อีกแล้ว (ไม่มี `Guid` ให้ส่ง) และเพิ่มฟิลด์ `soldByPlatform` (bool) บอกว่าเอกสารถูกขายผ่านแพลตฟอร์มนี้แล้วหรือไม่ โดยไม่แก้ค่า `paymentStatus` ที่ต้นทางรายงาน — `countMode=FAST` -> `totalRows`/`totalPages` เป็น `null` | มีแล้ว |
| SP ล่ม/ต่อไม่ได้ | `SqlException` ที่ไม่ใช่ error 50001-50009 (timeout/login/network/column drift) -> `UpstreamUnavailableException` -> **503** พร้อม detail คงที่ ไม่รั่วข้อความ SQL; connection string ที่ parse ไม่ได้ก็เข้าทาง 503 นี้เช่นกัน; input ที่ SP ปฏิเสธ (50001-50009) -> **400** — ไม่เปลี่ยนจากเดิม | มีแล้ว |
| อ่านเอกสารรายใบสดจากต้นทาง | `LookupDocumentQuery`/`LookupDocumentHandler` — internal เท่านั้น (`REQ-3.8`, ไม่ map เป็น route), ผู้เรียกคือ add-item / checkout เท่านั้น (create payment session **ไม่เรียก** — ใช้ `IDocumentSaleProbe` อย่างเดียว ดูหัวข้อ "กันขายเอกสารซ้ำ"); ยิง `ISpDocumentGateway.LookupAsync` ด้วย `@PaymentStatus='ALL'`, `@ProductGroup='ALL'` แล้วกรองแถวที่ `DocumentNo` ตรงเป๊ะในหน่วยความจำ — ไม่พบ -> `null`, ตรงมากกว่าหนึ่งแถว -> `SpDocumentAmbiguousException` (400) | มีแล้ว |
| mark เอกสารเป็น "ขายแล้ว" | **ไม่มีสถานะให้ mark อีกต่อไป** — เดิมมี `Product.MarkPaid` เขียนลงสำเนาของเราตอน order จ่ายสำเร็จ (`DocumentPaidOnOrderPaidConsumer`) ทั้งคู่ถูกลบ; "ขายแล้ว" ตอนนี้เป็นผลการ query สดจาก Orders ทุกครั้ง ไม่ใช่ flag ที่เก็บไว้ | เปลี่ยนกลไกทั้งหมด |
| แก้ไข/ถอนเอกสารจากการขาย | ไม่มี endpoint เหมือนเดิม — ไม่เคยมี | ยังไม่มี |
| อ่านรายตัว public | target: `GET /api/v1/products/{documentNo}` สำหรับหน้า detail ฝั่ง console — ยังไม่เริ่ม (route ต้องอยู่ใต้ `/api/v1/{area}` ตาม spec `api-route-scheme`, ไม่ใช่ audience-first `/api/producer/v1/...` ที่ retired ไปแล้ว) | ยังไม่มี |
| Product versioning + quote | target เดิม (`ProductVersion`/`ProductQuote`) อิงอยู่กับ aggregate ท้องถิ่นที่ถูกลบไปแล้ว — ถ้าจะทำต้องออกแบบใหม่บนฐาน "อ่านสดล้วน" นี้ ยังไม่เริ่ม | ยังไม่มี |

**สถานะ: มีแล้ว** — SP adapter ของจริงเขียนแล้วและใช้งานอยู่ (`products-sp-gateway`, ยังไม่เปลี่ยน)
โดยชี้ไปที่ database จำลอง `hippodb`/`mammothdb` (คนละ SQL Server container,
`external-sim-separate-containers`) ซึ่งรัน SP ตัวเดียวกับ contract ในทุก environment — การ
เชื่อมต่อ `motordb`/`centerdb` ของจริง **ไม่ใช่แค่เปลี่ยนค่า**
`SpDocument:MotorConnectionString`/`NonMotorConnectionString`: เส้นทางนั้นปิดอยู่สี่ชั้น
(`docker-compose.prod.yml` ของ service `api` ไม่มี key `SpDocument__*` เลย, `HIPPO_DB_SERVER`/
`MAMMOTH_DB_SERVER` ถูกบังคับด้วย `:?`, `migrate-entrypoint.sh` บังคับ bootstrap sim ก่อน `api`
ขึ้นเสมอ, และ `build_conn` hardcode ชื่อ database/principal ของ sim) — ตั้งสองค่านั้นพร้อมกับ
`HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER` วันนี้จะโดน `docker/entrypoint.sh` guard บล็อกไม่ให้ container
ขึ้น (เขียน stderr ระบุชื่อตัวแปรแล้ว exit non-zero แทนการทับเงียบ ๆ)

---

## ตัวระบุเอกสาร: DocumentNo แทน Guid

เดิมทุกจุดในระบบอ้างเอกสารด้วย `Guid ProductId` ที่เรามินต์เองตอน upsert เข้า `shop.Products`
ตอนนี้ตาราง `shop.Products` ไม่มีแล้ว จึงไม่มี `Guid` ให้มินต์ — ทุกชั้นอ้างเอกสารด้วย
**`DocumentNo` (string) โดยตรง** ตามที่ต้นทางสะกดมา (trim หัวท้าย ไม่แปลงตัวพิมพ์)

- เทียบ `DocumentNo` สองค่าว่าเป็นเอกสารเดียวกัน: ตรงกันทั้งสตริงหลัง trim หัวท้าย ไม่สนตัวพิมพ์
  ใหญ่เล็ก — ฝั่ง SQL เป็นเจ้าของกฎนี้จริง (collation `Thai_100_CI_AS` ของทุกคอลัมน์ `DocumentNo`
  ในฐานข้อมูล) ฝั่ง C# (`OrdinalIgnoreCase`/`InvariantCultureIgnoreCase` แล้วแต่จุด) เป็นแค่
  fast-path ที่ต้องเข้มกว่าหรือเท่ากันเท่านั้น ห้ามใช้ `COLLATE` ในประโยค query, `ToUpper()`,
  หรือ `StringComparison` ใน LINQ ที่ EF แปลไม่ได้
- `shop.CartItems`/`shop.CheckoutSessionItems`/`shop.OrderItems` เก็บ `DocumentNo`/`SaleCode`/
  `ProductGroup` เป็นคอลัมน์ตรง ๆ แทน `ProductId` เดิม (ดูหัวข้อ Schema ด้านล่าง)
- `Carts.Domain.Items.Item.Id` (`Guid` ที่ domain มินต์เอง, ไม่เกี่ยวกับเอกสาร) กลายเป็นตัวระบุ
  ที่ route `DELETE`/`PUT` ของบรรทัดในตะกร้าใช้แทน — เพราะ `DocumentNo` จริงมี `/` และอักษรไทย
  ใส่เป็น path segment ไม่ได้ (`itemId`, ไม่ใช่ `documentNo`, ไม่ใช่ `productId`)
- `Contracts.CheckoutConfirmedItem`/`GetOrders`/`GetOrderDetail`/`OrderSummaryReader`/
  `GET /orders/{token}/summary` (anonymous) ทั้งหมดอ้างเอกสารด้วย `documentNo` แทน `productId`
  แล้ว — ไม่มี endpoint หรือ payload ใดที่ยังรับ/คืน `productId` ชนิด `Guid`

---

## กันขายเอกสารซ้ำ: IDocumentSaleProbe

ต้นทางเป็น read-only ล้วน — ไม่มีวันรู้ว่าเราขายเอกสารไปหรือยัง ระบบจึง**อนุมาน**จากประวัติ
`Orders`/`PaymentSessions` ของเราเองทุกครั้งที่ต้องตัดสินใจว่าเอกสารใบหนึ่งขายได้หรือไม่ ผ่านพอร์ต
`IDocumentSaleProbe` ที่อยู่ใน `BuildingBlocks.Application` (ไม่ใช่ในโมดูล Products — มี 3 ผู้ใช้
คนละโมดูล: Products.Application ตอน list, Hosts ตอน add-item/checkout, Payments.Application ตอน
สร้าง payment session)

- **`DocumentKey(DocumentNo, ProductGroup)`** — คู่ที่ใช้ตัดสินว่าใบเดียวกัน (ความไม่ซ้ำข้าม
  Motor/NonMotor เป็น convention ไม่ใช่ DB constraint จึงต้องเทียบทั้งคู่)
- **`DocumentSaleState`**: `Sellable` (ไม่มี order ถือ) / `Sold` (order สถานะ `Paid` ถือ — ถาวร)
  / `PaymentInFlight` (order มี payment session ที่ยังชำระได้ หรือ PSP ยืนยันแล้วแต่ order ยัง
  ไม่พลิกเป็น `Paid` — หมดฤทธิ์เองเมื่อ session พ้นอายุ ไม่ต้องมี background job มาแก้แถว)
- **`DocumentSaleStatus`** คืนเหตุผล + order ที่ถือไว้ (`HeldByOrderId`) ไม่ใช่ `bool` — แต่ผู้เรียก
  ต้อง**ไม่ส่ง `HeldByOrderId` กลับไปให้ client เห็น** เพราะผู้ถืออาจเป็น merchant อื่น
- อ่านครั้งเดียวต่อคำขอ ข้ามทุก key (ไม่ใช่ N+1 ต่อเอกสาร) และข้ามทุก merchant (เอกสารที่ merchant
  อื่นขายไปแล้ว ก็ยังถือว่าขายแล้ว) ผ่าน `IgnoreQueryFilters()` — adapter จริงคือ
  `Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs` เขียนด้วย LINQ (ไม่ใช่ raw SQL เพื่อให้
  รันบน SQLite ของ `Hosts.Tests` ได้ด้วย) join `shop.OrderItems`/`shop.Orders`/`txn.PaymentSessions`

ด่านที่เรียก probe:

| ด่าน | เอกสารขายไม่ได้ | เอกสารต้นทางรายงาน `PAID` |
|---|---|---|
| `GET /products` (`paymentStatus=UNPAID`) | ตัดออกจากรายการ, ตั้ง `soldByPlatform=true` เมื่อขอ `ALL`/`PAID` | ไม่แก้ `paymentStatus` ที่ต้นทางว่า — แค่คงอยู่ในรายการ |
| `POST /carts/{cartId}/items` | 400 | 400 |
| `POST /checkouts` | 409, ไม่สร้าง checkout session | 409 |
| `POST /payments/sessions` (สร้างก่อน mint charge กับ PSP) | 409, ไม่มินต์ charge | (ตรวจซ้ำผ่าน probe เท่านั้น ไม่เห็นค่าต้นทางตรงนี้) |

หลังจ่ายเงินสำเร็จ `IDoubleSellAuditor` (`Orders.Application` ประกาศพอร์ต, implement ที่
`Persistence.MerchantRuntime/Orders/DoubleSellAuditor.cs` เพราะ `Orders.Application` reference
logging ไม่ได้) ถูกเรียกจาก `OrderPaidConsumer` ทุกครั้งที่ order พลิกเป็น `Paid` — ถ้าเอกสารใบ
เดียวกันมี order สถานะ `Paid` มากกว่าหนึ่งใบ (ไม่นับ order ที่กำลังประมวลผลเป็นใบที่สอง กัน
outbox redelivery ปลุกคนกลางดึกทุกรอบ) จะ `LogCritical` พร้อมเลขเอกสารและ order ทั้งสองใบ — เป็น
กลไกแจ้งเตือนคน ไม่ใช่กลไกกันเหตุการณ์นั้นเกิด (จ่ายที่ PSP ไปแล้วตอนนี้เรียก)

---

## Endpoint contract

`GET /products` ยังเป็น endpoint เดียวของโมดูล — รับ `page`/`limit` + `productFilters` (JSON ใน
query string) กำหนดโดย `ProductFilterDto`:

- ไม่มี member `saleCode` อีกต่อไป — server กำหนดจาก `IActorContext.SaleCode` (claim
  `sale_code` ของ merchant user ที่ล็อกอินอยู่) เสมอ ถ้า client ใส่มาใน JSON จะถูกเมินเงียบ ๆ
  (deserializer ข้าม member ที่ไม่รู้จัก)
- merchant user ที่ไม่มี `SaleCode` ผูกอยู่เลย -> **403** ตรวจ**ก่อน**แม้แต่ parse `productFilters`
  กันคนไม่มีสิทธิ์ probe รูปแบบ filter ได้
- ต้องมี `productGroup` หรือ `insuranceType` อย่างน้อยหนึ่งอย่าง ไม่งั้น 400 (สองฝั่ง Motor/NonMotor
  เป็นคนละ stored procedure ไม่มี default ให้เดา) — ระบุทั้งคู่ต้องไม่ขัดแย้งกัน
- filter อื่นตาม §2 ของเอกสาร SP: `searchText`/`insuredName`/`policyNo`/`applicationNo`/
  `documentType`/`coverageStartFrom`/`coverageStartTo`/`coverageEndFrom`/`coverageEndTo`/
  `paidDateFrom`/`paidDateTo`; `paymentStatus` (`UNPAID`\|`PAID`\|`ALL`, absent = `UNPAID`);
  `countMode` (`EXACT`\|`FAST`, absent = `EXACT`)

`POST /carts/{cartId}/items`, `DELETE /carts/{cartId}/items/{itemId:guid}`,
`PUT /carts/{cartId}/items/{itemId:guid}` อยู่ในโมดูล Carts แต่พึ่งพา Products โดยตรง:

- body ของ add-item เปลี่ยนจาก `{ productId, quantity }` เป็น
  `{ documentNo, productGroup, quantity }` — handler เรียก `LookupDocumentQuery` สดเพื่อตั้งราคา
  จาก `TotalPremium` ที่ต้นทางคืน (ไม่รับราคาจาก client), ตรวจ `PaymentStatus=PAID` (400) และ
  probe เอกสารขายแล้ว (400) ก่อนเรียก `AddItemToCartCommand`
- route ของการลบ/แก้จำนวน ใช้ `itemId` (`Guid` ที่ `Cart.AddItem` มินต์เอง) แทน `productId` เดิม
- `GET /carts/{cartId}` คืนแต่ละบรรทัดพร้อม `itemId`, `documentNo`, `saleCode`, `productGroup`

`LookupDocumentQuery`/`LookupDocumentHandler` (`Products.Application/LookupDocument.cs`) **ไม่ถูก
map เป็น HTTP route** — เป็น internal query ที่ add-item และ checkout (อ่านสดต่อบรรทัดตอนเริ่ม
checkout) ใช้ร่วมกันเท่านั้น การสร้าง payment session **ไม่เรียก** `LookupDocumentQuery`/
`ISpDocumentGateway.LookupAsync` เลย — ใช้แค่ `IDocumentSaleProbe` ตรวจกับประวัติ order ของเรา
เอง (ดูหัวข้อ "กันขายเอกสารซ้ำ") จึงไม่ต้องพึ่งความพร้อมของต้นทางในขั้นนี้

---

## Error handling

| เงื่อนไข | กลไก | ผลลัพธ์ |
|---|---|---|
| `documentNo` ว่าง/ยาวเกิน 150 | `ArgumentException` ที่ boundary | 400 |
| `documentNo` ยาวเกิน 100 (ขีดของ `@SearchText`) | ปฏิเสธก่อนเรียก gateway (adapter **และ** handler) | 400 |
| ต้นทางไม่มีเอกสารนั้น (add-item) | `LookupAsync` คืน `null` | 400 |
| ต้นทางไม่มีเอกสารนั้น (checkout) | `LookupAsync` คืน `null` | 409 |
| ต้นทางคืนแถวตรงเป๊ะมากกว่าหนึ่งแถว | `SpDocumentAmbiguousException : ArgumentException` + log error | 400 |
| ต้นทางคืน `PaymentStatus=PAID` (add-item) | ตรวจที่ endpoint หลัง lookup | 400 |
| ต้นทางคืน `PaymentStatus=PAID` (checkout) | ตรวจต่อบรรทัด | 409 |
| ต้นทาง raise 50001-50009 | `SpDocumentSearchRejectedException : ArgumentException` | 400 |
| ต้นทางต่อไม่ติด/timeout/5xx/connection string parse ไม่ได้ | `UpstreamUnavailableException` | 503 |
| เอกสารขายแล้ว/กำลังถูกจ่าย (add-item) | probe คืน `Sold`/`PaymentInFlight` | 400 |
| เอกสารขายแล้ว/กำลังถูกจ่าย (checkout) | probe | 409 |
| เอกสารขายแล้วโดย order อื่น (payment session) | probe ก่อน mint charge | 409 |
| เอกสารซ้ำในตะกร้าเดียว | `Cart.AddItem` โยน `ArgumentException` | 400 |
| `itemId` ไม่มีในตะกร้า | `NotFoundException` | 404 |
| merchant user ไม่มี `SaleCode` | ตรวจที่ endpoint **ก่อน** parse `productFilters` | 403 |
| ค่าที่จะผูกกับ `@SaleCode` ยาวเกิน 20 หรือมีอักขระนอก ASCII ที่พิมพ์ได้ | `SaleCodeBindingException` (ไม่ใช่ `ArgumentException` — เป็นบั๊กของเราเอง ไม่ใช่ของ client) | 500 + log |

ข้อความ 400/409 ทุกจุดที่มาจากขั้นกันขายซ้ำ**ไม่เปิดเผยรหัสหรือชื่อ merchant อื่น** —
`HeldByOrderId` ที่ probe คืนมาใช้เฉพาะใน log server-side เท่านั้น

---

## Schema — คอลัมน์เอกสารบน CartItems / CheckoutSessionItems / OrderItems

**ไม่มีตาราง `shop.Products` แล้ว** (DROP ใน migration `DropProductCatalogueCutoverDocumentNo`)
แต่ละบรรทัดในตะกร้า/checkout session/order เก็บฟิลด์เอกสารเป็นคอลัมน์ของตัวเองแทน — snapshot
มาจากค่าที่ต้นทางคืนตอน add-item/checkout ไม่ใช่การอ้างอิงสดไปยังที่ใด

| คอลัมน์ | ชนิด | อยู่ในตาราง | หมายเหตุ |
|---|---|---|---|
| `DocumentNo` | `nvarchar(150)` NOT NULL | `CartItems`, `CheckoutSessionItems`, `OrderItems` | ตัวระบุเอกสาร — trim หัวท้ายก่อนบันทึกเสมอ |
| `SaleCode` | `varchar(20)` NOT NULL | `CartItems` เท่านั้น | ค่าที่ต้นทางคืนตอน add-item (ไม่ใช่ที่ client ส่ง) |
| `ProductGroup` | `varchar(10)` NOT NULL | `CartItems`, `CheckoutSessionItems`, `OrderItems` | wire value ของ enum (`CMI`/`VMI`/`FIRE`/`MISC`) |
| `DocumentType` | `varchar(20)` NOT NULL | `CheckoutSessionItems`, `OrderItems` | `POLICY`/`APPLICATION`/`RENEWAL`/`ENDORSEMENT` |
| `PolicyNumber` | `varchar(150)` | `CheckoutSessionItems`, `OrderItems` | snapshot ตอนเริ่ม checkout |
| `StartDate`/`EndDate` | `datetime2(0)` | `CheckoutSessionItems`, `OrderItems` | snapshot ตอนเริ่ม checkout |

`IX_OrderItems_DocumentNo (DocumentNo) INCLUDE (OrderId, ProductGroup)` — index เดียวที่
`DocumentSaleProbe`/`DoubleSellAuditor` seek, **ไม่ unique** (order ที่ยกเลิกแล้วกับ order ที่ขาย
จริงถือเอกสารเดียวกันได้โดยชอบธรรม — unique จะยิงตอน INSERT ของ order ใหม่)

เอกสารเอง (ทุกฟิลด์ของ §5.2: `PolicyYear`/`ReferenceBranch`/`SaleFullName`/`BrokerCode`/`ShowName`/
`NetPremium`/`Stamp`/`TaxVat`/`TotalPremium`/`CommissionPercent`/`CommissionAmount`/`PaidDate`/
`LicensePlateNumber`/ฯลฯ — 32 ฟิลด์ทั้งหมด) **ไม่มีการมิเรอร์ทั้งชุดเก็บไว้ที่ใดอีกแล้ว** (แคตตาล็อก
32 ฟิลด์แบบเดิมถูกลบไปกับ `shop.Products`) — แต่ไม่ใช่ทุกฟิลด์หายไปจากฐานข้อมูลเรา: 6 ฟิลด์ตาม
ตารางด้านบน (`DocumentNo`/`ProductGroup`/`DocumentType`/`PolicyNumber`/`StartDate`/`EndDate`) ยัง
snapshot ลงคอลัมน์ของบรรทัดใน purchase flow และ `TotalPremium` ยัง persist อยู่ในรูป `UnitPrice`
ของ `CartItems` (mint เป็น `Money` ตอน add-item) ส่วนที่เหลือ (`SaleFullName`/`BrokerCode`/
`NetPremium`/`Stamp`/`TaxVat`/`CommissionAmount`/`CommissionPercent`/`LicensePlateNumber`/ฯลฯ)
ไม่ถูกเก็บที่ใดจริง ๆ — อ่านสดผ่าน `Products.Application/DocumentView.cs` ทุกครั้งที่ต้องใช้
ดูนิยามเต็มที่โครงสร้างไฟล์ด้านล่าง

funnel ธุรกิจ: `shop.Carts → shop.CheckoutSessions → shop.Orders → txn.PaymentSessions` — ข้าม
schema ที่ขั้นสุดท้าย (`PaymentSessions` แม็ปเข้า `SchemaNames.Txn` ไม่ใช่ `shop`) และไม่มี
`Products` อยู่ในลำดับนี้อีกต่อไปเพราะไม่มีตารางให้เป็นจุดเริ่ม

---

## โครงสร้างไฟล์ (`src/Modules/Products/`)

> `Product.cs`/`ProductInput.cs`/`IProductRepository.cs`/`ProductConfiguration.cs` (ทั้งสองที่)/
> `CreateProductCommand.cs`/`GetProductById.cs`/`DocumentPaidOnOrderPaidConsumer.cs` **ถูกลบทั้งหมด**
> ใน `products-external-source-of-truth` — ไม่มี aggregate ให้ persist อีกแล้ว

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `DocumentView.cs` | Application | DTO กลาง — มิเรอร์ §5.2 ทั้ง 32 ฟิลด์หลัง mapper ตรวจ/แปลง wire enum แล้ว; ไม่มี `Id` — ทุกจุดของ purchase path อ่านเอกสารผ่านตัวนี้ |
| `ListProducts.cs` | Application | `ListProductsQuery`/`Handler` -> `ProductPage`; `ProductListItem` (คืน `ProductPage`, ตัด `Id`, เพิ่ม `SoldByPlatform`) + `ProductFilterDto` (ตัด `SaleCode` ออก, ไม่บังคับส่งอีกต่อไป) |
| `LookupDocument.cs` | Application | `LookupDocumentQuery`/`Handler` — อ่านเอกสารรายใบสด, internal เท่านั้น (ไม่ map route) |
| `Ports/ISpDocumentGateway.cs` | Application | พอร์ต — `SearchAsync` (list) + `LookupAsync` (รายใบ, ใหม่) |
| `Ports/SpDocumentContracts.cs` | Application | DTO ของ wire: `SpDocumentSearchRequest`/`SpDocumentLookupRequest`/`SpPaginationMetadata`/`SpDocumentItem`/`SpDocumentSearchResult` |
| `Ports/SpDocumentItemMapper.cs` | Application | แปลง `SpDocumentItem` -> `DocumentView?` (ข้ามแถวเสีย + dedupe `DocumentNo` ในหน้าเดียว) — คืน `DocumentView` แทน `ProductInput` เดิม |
| `Ports/SpDocumentMatch.cs` | Application | `SelectExactlyOne` — กรองแถวที่ `DocumentNo` ตรงเป๊ะจากผลของ `LookupAsync`, ไม่พบ -> `null`, มากกว่าหนึ่ง -> throw |
| `Ports/SpDocumentAmbiguousException.cs` | Application | lookup ตรงมากกว่าหนึ่งแถว -> 400 |
| `Ports/SaleCodeBindingException.cs` | Application | ค่า `SaleCode` ที่จะผูกกับ `@SaleCode` ถูกตัดทอน/มีอักขระนอก ASCII -> 500 (บั๊กของเรา ไม่ใช่ของ client) |
| `Ports/SpDocumentSearchRejectedException.cs` | Application | ต้นทาง raise 50001-50009 -> 400 (ไม่เปลี่ยนจากเดิม) |
| `Products.Domain/{ProductGroup,DocumentType,PaymentStatus,InsuranceType}.cs` | Domain | enum 4 ตัวเท่านั้น — ยังเป็น published language ของโมดูล แม้ aggregate จะถูกลบไปแล้ว |
| `Products.Infrastructure/Sp/SpDocumentGateway.cs` / `SpDocumentOptions.cs` | Infra | adapter ADO.NET (`Microsoft.Data.SqlClient` + `CommandType.StoredProcedure`) ยิง `usp_Motor_SearchDocument`/`usp_NonMotor_SearchDocument`; `LookupAsync` วนอ่านทุกหน้าที่ `@SearchText` LIKE คืนมา (สูงสุด 40 หน้า) แล้วส่งให้ `SpDocumentMatch` เลือก |
| `Products.Infrastructure/ProductsModuleRegistration.cs` | Infra | ลงทะเบียนแค่ `ISpDocumentGateway` เป็น singleton — **ไม่มี repository ให้ลงทะเบียนอีกแล้ว** (ของเดิมเคยชี้ไป `Persistence.MerchantRuntime` ก็ถูกลบไปด้วย) |

นอกโมดูล (cross-module ports/adapters ที่ Products ใช้ร่วม):

| ไฟล์ | ชั้น | บทบาท |
|------|------|-------|
| `BuildingBlocks.Application/IDocumentSaleProbe.cs` | Application (cross-module) | พอร์ต `ProbeAsync` — 3 ผู้ใช้คนละโมดูล จึงอยู่ที่ `BuildingBlocks` ไม่ใช่ในโมดูลใดโมดูลหนึ่ง |
| `Persistence.MerchantRuntime/Orders/DocumentSaleProbe.cs` | Infra | adapter จริงของ `IDocumentSaleProbe` — LINQ + `IgnoreQueryFilters()` ข้าม merchant |
| `Persistence.MerchantRuntime/Orders/DoubleSellAuditor.cs` | Infra | adapter ของ `Orders.Application/IDoubleSellAuditor.cs` — log critical เมื่อเอกสารเดียวกันมี order `Paid` สองใบ |

`Products.Application` reference แค่ `Products.Domain`, `Contracts`, `BuildingBlocks.Application`
— ไม่แตะ `.Domain` โมดูลอื่นเลย (ไม่มี cross-module `.Domain` reference)

## ข้อยกเว้นจาก convention ทั่วไปของ repo

- **`GET /products` เป็น read-only แท้จริงตอนนี้** — ตรงข้ามกับสถาปัตยกรรมเดิม (ที่เคยเป็น
  "endpoint อ่านแล้วเขียน" เพราะ upsert สำเนากลับเข้า `shop.Products`) HTTP GET ปกติทั่วไปคือ
  read-only อยู่แล้ว โมดูลนี้จึงกลับมาตรงกับ convention ของ repo แล้ว ไม่ใช่ข้อยกเว้นอีกต่อไป
- **ไม่มี SFS (Search/Filter/Sort) ทั่วไป** — รับแค่ `page`/`limit` (cap 25) + typed
  `productFilters`, order คงที่ด้วย `DocumentNo` (ไม่ใช่ `CreatedAt DESC` แบบ list endpoint อื่น)
  `ProductSfs.cs` ไม่มีอยู่ในโค้ด ตัวอย่างโค้ดแบบ SFS เดิมใน `search-filter-sort.md` §7/§12.2 เป็น
  **ตัวอย่างเชิงสมมติของ pattern เท่านั้น** ไม่ตรงกับโค้ด Products จริง
- **ไม่มี concurrency token** — ไม่มี aggregate ให้มี `RowVersion` อีกแล้ว (ไม่มีตารางของโมดูลนี้
  ในฐานข้อมูลเลย)
- **`IDocumentSaleProbe` อ่านข้าม merchant floor โดยตั้งใจ** ผ่าน `IgnoreQueryFilters()` บน
  `shop.OrderItems`/`shop.Orders`/`txn.PaymentSessions` (เอกสารที่ merchant อื่นขายไปแล้ว ก็ยัง
  ถือว่าขายแล้ว — REQ-5.2) และ**ไม่ยิง `ISecurityTelemetry.Emit`** เพราะ probe ทำงานทุกครั้งที่
  ค้นแคตตาล็อก/add-item/checkout/สร้าง payment session การ emit ทุกครั้งจะกลบสัญญาณ denial จริง
  จนไร้ประโยชน์ — เหตุผลนี้เขียนกำกับไว้ที่ entry ของ `BypassPrimitiveTests` allowlist
- **`LookupDocumentQuery` ไม่ถูก map เป็น HTTP route โดยตั้งใจ** — internal เท่านั้น ต่างจาก query
  ส่วนใหญ่ในโมดูลอื่นที่มัก map ตรงเป็น endpoint
