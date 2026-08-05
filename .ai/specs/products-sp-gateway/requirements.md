# Requirements: products-sp-gateway

> Status: approved 2026-07-31, amended 2026-07-31 (spec-architect design critique B1/B2/M6/M7/M9 — ดู design.md "Design review log")
> Scope: ชั้น DTO/port แยก Domain ของ repo ออกจาก wire contract ของ VCentralPay SP + adapter ADO.NET จริง + database จำลอง `hippodb`/`mammothdb` (SP เต็มตาม contract) ใน pol-db container — อ้าง `docs/reference/vcentralpay-sp-quick-reference.pdf` v1.0 (§1-§6) และแผนที่ approve `~/.claude/plans/src-modules-products-dto-hidden-sketch.md`

## บริบท

`Product` เป็น mirror ของ §5.2 แบบ 1:1 แล้ว (spec `products-sp-53-alignment`) แต่ list ยังอ่านจาก
`shop.Products` ตรง ๆ — ไม่มีชั้นแยกระหว่าง Domain กับระบบต้นทางซึ่งเก็บเอกสารจริงใน SQL Server
2 instance ที่เราไม่ได้เป็นเจ้าของ (`motordb` บน server hippo, `centerdb` บน server mammoth)
งานนี้คือ follow-up "SP adapter จริง" + "@CountMode/§5.1 envelope" ที่จองไว้ใน
`products-sp-53-alignment/design.md` — จำลอง database ทั้งสองไว้ในระบบเราเอง (ชื่อ `hippodb`/`mammothdb`
ตามชื่อ server จริง ให้ชัดว่าเป็นตัวจำลอง) เพื่อให้งานส่วนอื่นเดินต่อได้โดยไม่ต้องรอ upstream;
วันเชื่อมระบบจริงเปลี่ยนแค่ connection string (InitialCatalog -> motordb/centerdb บน server จริง)

การตัดสินใจที่ล็อกจาก plan mode: (1) จำลองด้วย SP จริงใน SQL container ไม่ใช่ fake in-process,
(2) `GET /api/v1/products` ค้นสดผ่าน gateway แล้ว upsert ลง `shop.Products`,
(3) full spec workflow มี gate, (4) sim DB รันทุก environment รวม prod-like

## Requirements (EARS)

## REQ-1: Database จำลอง `hippodb` / `mammothdb`

**User Story:** ในฐานะทีมพัฒนา ฉันต้องการ database ภายนอกจำลองที่เหมือนจริงในระบบของเราเอง เพื่อให้งานส่วนอื่นเดินต่อได้โดยไม่ต้องรอ upstream จริง

- 1.1 THE SYSTEM SHALL มีสคริปต์ `docker/bootstrap/02-external-sim.sql` แบบ idempotent (รันซ้ำได้ผลเท่าเดิม) ที่สร้าง database `hippodb` (จำลอง `motordb` บน server hippo) และ `mammothdb` (จำลอง `centerdb` บน server mammoth) เมื่อยังไม่มี
- 1.2 THE SYSTEM SHALL สร้างตาราง `dbo.Documents` ในแต่ละ database มีคอลัมน์ตาม §5.2 ชนิดตรงตามเอกสาร (`decimal(19,2)`/`(19,6)`, `datetime2(0)`, varchar/nvarchar ตามขนาด) **ยกเว้น `InsuranceType`** ซึ่งเป็นค่า derive ที่ SP คืนคงที่ต่อฝั่ง (Motor SP -> 'Motor', Non-Motor SP -> 'NonMotor') ไม่ใช่คอลัมน์จริง บวกคอลัมน์ `BranchCode varchar(3)` (F6)
- 1.3 WHERE ฝั่ง `mammothdb` (Non-Motor), THE SYSTEM SHALL ใช้ตาราง `dbo.Documents` ตารางเดียวแทน topology จริง (centerdb + firewebdb + miscwebdb) — contract ที่ต้อง honor คือ output ของ SP ไม่ใช่โครงสร้างภายใน; ระบุ deviation ใน comment หัวไฟล์
- 1.4 THE SYSTEM SHALL seed ข้อมูลปลอม deterministic ที่เลียนเฉพาะ *รูปแบบ* (ห้ามคัดลอกค่าจากระบบจริง — กติกา `demo-seed-data/design.md`) ด้วยวันที่สัมพัทธ์ `GETDATE()` ให้ครอบเคส: ใน/นอก window 6 เดือน, RENEWAL ใน/นอก window 2 เดือน, PAID+PaidDate / UNPAID, แถวมี/ไม่มี `LicensePlateNumber`, และใช้ `SaleCode` ชุดเดียวกับ `seed-demo.sql` เพื่อให้ demo flow เดิมเดินต่อได้
- 1.5 THE SYSTEM SHALL มี self-check ท้ายสคริปต์ที่ทำให้ `sqlcmd -b` ล้มเหลวเมื่อ object สำคัญหรือจำนวนแถว seed ไม่ตรงคาด (แบบเดียวกับ `seed-demo.sql`)

## REQ-2: SP จำลองต้องตรง contract §2 + §5 + §6 ทุกช่อง

**User Story:** ในฐานะทีม API ฉันต้องการ SP จำลองที่ประพฤติเหมือนของจริงทุกกติกา เพื่อให้ adapter ที่เขียนวันนี้เป็นโค้ด production จริงที่สลับไป upstream ได้โดยไม่แก้โค้ด

- 2.1 THE SYSTEM SHALL สร้าง `hippodb.dbo.usp_Motor_SearchDocument` และ `mammothdb.dbo.usp_NonMotor_SearchDocument` ด้วย `CREATE OR ALTER PROCEDURE` รับพารามิเตอร์ครบ 18 ตัว ชนิด/ขนาดตรงตาราง §2 (F1)
- 2.2 THE SYSTEM SHALL normalize input ภายใน SP: `@PageNo` NULL/<1 -> 1; `@PageSize` NULL/<1 -> 25, >25 -> 25; `@PaymentStatus` NULL -> 'UNPAID'; `@DocumentType` NULL -> 'ALL'; `@ProductGroup` NULL -> 'ALL'; `@CountMode` NULL -> 'EXACT'
- 2.3 WHEN `@PaidDateFrom` หรือ `@PaidDateTo` มีค่า, THE SYSTEM SHALL บังคับผลลัพธ์เหลือเฉพาะเอกสาร PAID (§2)
- 2.4 IF input ผิดกติกา THEN THE SYSTEM SHALL `THROW` ด้วยเลขตาม §6: 50001 invalid `@DocumentType`, 50002 invalid `@ProductGroup` (Motor รับ CMI|VMI|ALL, Non-Motor รับ FIRE|MISC|ALL), 50003 `@PaidDateFrom > @PaidDateTo`, 50004 `@BranchCode` ว่าง/NULL, 50005 `@SaleCode` ว่าง/NULL, 50006 invalid `@CountMode`, 50007 invalid `@PaymentStatus`, 50008 `@CoverageStartFrom > @CoverageStartTo`, 50009 `@CoverageEndFrom > @CoverageEndTo`
- 2.5 THE SYSTEM SHALL จำกัด search window ผ่าน `GETDATE()`: Motor เอกสารทั่วไป `StartDate` ย้อนหลังไม่เกิน 6 เดือน; Motor RENEWAL ใช้ `EndDate` ในช่วง [วันนี้, วันนี้ + 2 เดือน); Non-Motor ทุก DocumentType ใช้ `StartDate` ย้อนหลัง 6 เดือน — ทิศ RENEWAL ตีความเดียวกับ sp-53 REQ-6.2
- 2.6 WHEN `@SearchText` มีค่า, THE SYSTEM SHALL ค้นแบบ partial บน `DocumentNo`/`PolicyNumber`/`ApplicationNumber`/`EndorsementNumber` โดยรวม `LicensePlateNumber` เฉพาะ Motor SP (§3 รวม / §4 ไม่รวม)
- 2.7 THE SYSTEM SHALL คืน 2 result sets ตามลำดับตายตัวเสมอ: (1) pagination metadata ครบ 8 field ตาม §5.1, (2) รายการเอกสารครบ field ตาม §5.2 เรียง `ORDER BY DocumentNo` + `OFFSET/FETCH`
- 2.8 WHILE `@CountMode = 'EXACT'`, THE SYSTEM SHALL คืน `TotalRows` = `COUNT_BIG` ของทั้งชุดผลลัพธ์ และ `TotalPages` = CEILING(TotalRows / PageSize)
- 2.9 WHILE `@CountMode = 'FAST'`, THE SYSTEM SHALL คืน `TotalRows` = NULL และ `TotalPages` = NULL โดย `HasNextPage` ยังถูกต้อง — result set 2 คืนไม่เกิน `@PageSize` แถวเสมอ; การดึง PageSize + 1 เป็นเทคนิคภายในเพื่อคำนวณ `HasNextPage` แล้วทิ้งแถวเกิน (F5)
- 2.10 THE SYSTEM SHALL คืน `SearchWindowMonths` = 6 ใน metadata (§5.1)
- 2.11 THE SYSTEM SHALL validate `@BranchCode` (ว่าง -> 50004) โดยไม่ filter แถวตาม branch — เอกสารไม่ระบุ semantics การกรอง; บันทึกเป็น assumption
- 2.12 THE SYSTEM SHALL filter ผลลัพธ์ตาม predicate ครบชุดนี้ (F2): `@SaleCode` exact match (trim แล้ว) — แกน scope ของการค้น; `@InsuredName` partial (LIKE) บน `ShowName`; `@PolicyNo` exact บน `PolicyNumber`; `@ApplicationNo` exact บน `ApplicationNumber`; `@DocumentType`/`@ProductGroup` exact เมื่อค่า normalize แล้วไม่ใช่ 'ALL'; `@PaymentStatus` exact เมื่อไม่ใช่ 'ALL'; `@CoverageStartFrom`/`@CoverageStartTo` inclusive บน `StartDate`; `@CoverageEndFrom`/`@CoverageEndTo` inclusive บน `EndDate`; `@PaidDateFrom`/`@PaidDateTo` inclusive บน `PaidDate` (ควบกับ force PAID ตาม REQ-2.3)

## REQ-3: สิทธิ์ฐานข้อมูล + environment ที่รัน

- 3.1 THE SYSTEM SHALL สร้าง `USER pol_app` ในทั้งสอง database และ `GRANT EXECUTE` บน SP ทั้งสองตัว (§4.3) — SELECT บนตารางอาศัย ownership chaining ไม่ต้อง grant เพิ่ม
  - superseded โดย `sim-db-separate-logins` (`.pipeline/sim-db-separate-logins/spec.md`, 2026-08-05):
    principal ไม่ใช่ `pol_app` แล้ว — `hippodb` สร้าง `USER hippo_app`, `mammothdb` สร้าง `USER mammoth_app`
    คนละ login/password กัน; รูปสิทธิ์คงเดิมเป๊ะ (`GRANT EXECUTE` บน SP ของฝั่งตัวเองเท่านั้น + ownership
    chaining) — spec นี้ปิดแล้ว ข้อความ REQ เดิมคงไว้ ไม่แก้ย้อนหลัง
  - แก้เพิ่ม 2026-08-05: `GRANT SELECT ON dbo.Documents` ให้ `hippo_app` / `mammoth_app` ด้วย —
    เพราะ metadata visibility ของ SQL Server ซ่อนตารางออกจาก `sys.tables` ทั้งใบเมื่อ principal
    ไม่มีสิทธิ์ใด ๆ บนตาราง ทำให้ client แบบ GUI ที่ต่อด้วย credential ในไฟล์ `.env` (ตัวเดียวที่
    developer มี) เห็นเป็น "seed หาย"; แลกกับความเที่ยงของ sim ที่หย่อนลง (login จริงฝั่ง upstream
    เป็น EXECUTE-only) โดยโค้ด production ยังไม่ SELECT ตารางนี้ — `RawConnectionTests` ยังตรึง seam เดิม
- 3.2 THE SYSTEM SHALL รัน `02-external-sim.sql` ในทุก environment: docker-compose dev (service `pol-db-init`), CI integration ทั้ง GitHub และ GitLab, และ prod-like deploy — จนกว่าจะเชื่อม upstream จริง; ฝั่ง prod-like ข้อบังคับคือ "สคริปต์ต้องรันเสร็จก่อน API พร้อมรับ traffic" ส่วนกลไก (เพิ่ม mssql-tools ใน migrate image vs init service แบบ `pol-db-init`) ให้ design.md ตัดสิน (F4)
- 3.3 THE SYSTEM SHALL bootstrap database จำลองให้เสร็จก่อน `dotnet test` ใด ๆ ที่ boot host ต่อ :11433 (กัน parallel CREATE DATABASE race)
- 3.4 THE SYSTEM SHALL ไม่เพิ่ม env var / compose variable ใหม่ — connection string จำลอง derive จากของที่มีอยู่ (กัน render-check ทั้ง 2 CI workflow)

## REQ-4: ชั้น DTO/port แยก Domain จาก wire contract

**User Story:** ในฐานะเจ้าของ Domain ฉันต้องการ DTO ของ SP แยกจาก aggregate ของฉัน เพื่อให้ wire contract เปลี่ยนได้โดยไม่ลาม Domain และ Domain เปลี่ยนได้โดยไม่ผิด contract

- 4.1 THE SYSTEM SHALL มี port `ISpDocumentGateway` ใน `Products.Application/Ports/` มีเมธอดเดียว `SearchAsync(SpDocumentSearchRequest, CancellationToken)` คืน `SpDocumentSearchResult` (แม่แบบ: `Payments.Application/Ports/IPspAdapter`)
- 4.2 THE SYSTEM SHALL มี `SpDocumentSearchRequest` mirror §2: 17 พารามิเตอร์บน record + routing key `Target` (`InsuranceType`) ที่ server คำนวณเอง — `@BranchCode` (ตัวที่ 18) ไม่อยู่บน request แต่ถูกเติมที่ adapter จาก `SpDocumentOptions` (REQ-6.6) เพื่อไม่ให้ Application ต้องพึ่ง options infrastructure; client ต้องไม่มีช่องเลือก connection/SP (§1.1) (F1, amended B2)
- 4.3 THE SYSTEM SHALL มี `SpPaginationMetadata` ตรง §5.1 ทุก field โดย `TotalRows`/`TotalPages` เป็น `long?`
- 4.4 THE SYSTEM SHALL มี `SpDocumentItem` ครบทุก field §5.2 แบบ nullable ทั้งหมด โดย `InsuranceType`/`SourceSystem`/`DocumentType`/`PaymentStatus` เป็น raw string — wire truth แยกจาก Domain enum
- 4.5 THE SYSTEM SHALL มี `SpDocumentSearchRejectedException` (สืบทอด `ArgumentException`) พก `SpErrorNumber` เพื่อได้ 400 ProblemDetails จาก handler เดิม — wire detail เป็น fixed string ของ handler (`ProblemDetailsExceptionHandler` ห้าม echo exception message by design); message ที่เราเขียนใช้ฝั่ง server/log/test เท่านั้น (amended M6)
- 4.6 THE SYSTEM SHALL มี `UpstreamUnavailableException` ใน `BuildingBlocks.Application` ที่ `ProblemDetailsExceptionHandler` map เป็น 503 โดยไม่รั่วข้อความ SQL/infra กลับ client
- 4.7 THE SYSTEM SHALL ใช้ type ของ SP wire (`SpDocument*`) เฉพาะภายใน `Products.Application` (port + mapper + handler) และ `Products.Infrastructure` (adapter) เท่านั้น — `Products.Domain`, API response ของ Hosts, `src/Contracts` และโมดูลอื่นทั้งหมดต้องใช้ type ที่ repo กำหนดเอง (`ProductInput` / `Product` / `ProductListItem` / `ProductPage`); ค่าใน envelope เป็นการ *คัดลอกค่า* จาก `SpPaginationMetadata` เข้า `ProductPage` ไม่ใช่ส่งต่อ type
- 4.8 WHEN contract ของ upstream เปลี่ยน (เพิ่มคอลัมน์ / เปลี่ยนชนิด / เพิ่มพารามิเตอร์), THE SYSTEM SHALL จำกัดจุดแก้ไว้ที่ `SpDocumentContracts` + mapper + sim SP เท่านั้น — `Products.Domain` และ API response shape ต้องไม่ถูกบังคับให้เปลี่ยนตาม (เปลี่ยนได้เฉพาะเมื่อตั้งใจรับ field ใหม่เป็นการตัดสินใจแยก)

## REQ-5: Adapter ADO.NET จริง

- 5.1 THE SYSTEM SHALL implement `SpDocumentGateway` ใน `Products.Infrastructure/Sp/` ด้วย `Microsoft.Data.SqlClient` + `CommandType.StoredProcedure` + parameter typed ตามชนิด/ขนาด §2
- 5.2 THE SYSTEM SHALL เลือก connection string จาก `request.Target`: Motor -> `MotorConnectionString`, NonMotor -> `NonMotorConnectionString`
- 5.3 THE SYSTEM SHALL อ่าน result set ตามลำดับตายตัว: RS1 หนึ่งแถว -> `NextResultAsync()` -> RS2 อ่านคอลัมน์ด้วย `GetOrdinal` ตามชื่อ
- 5.4 IF RS1 ไม่มีแถว THEN THE SYSTEM SHALL โยน `UpstreamUnavailableException`
- 5.5 IF `SqlException.Number` อยู่ใน 50001..50009 THEN THE SYSTEM SHALL โยน `SpDocumentSearchRejectedException` (-> 400)
- 5.6 IF `SqlException` อื่นใด (timeout / login / permission / connectivity) THEN THE SYSTEM SHALL log รายละเอียดฝั่ง server แล้วโยน `UpstreamUnavailableException` (-> 503)
- 5.7 THE SYSTEM SHALL มี `SpDocumentOptions` (section `SpDocument`): `BranchCode` default `"000"`, `MotorConnectionString?`, `NonMotorConnectionString?`, `CommandTimeoutSeconds` default 15 — ห้ามใช้ `.ValidateOnStart()` (Hosts.Tests boot จริง 17 ตัว)
- 5.8 WHEN connection string ไม่ถูก config, THE SYSTEM SHALL derive จาก `ConnectionStrings:App` โดยเปลี่ยน `InitialCatalog` เป็น `hippodb`/`mammothdb` ผ่าน `PostConfigure` (default = sim; override เป็น server จริงได้ทาง config)
- 5.9 THE SYSTEM SHALL ลงทะเบียน gateway เป็น singleton ใน `ProductsModuleRegistration.AddProductsModule()` และ pin `Microsoft.Data.SqlClient` ใน `Directory.Packages.props` เวอร์ชันเดียวกับที่ EF SqlServer อ้างแบบ transitive

## REQ-6: Routing Motor/NonMotor + filter surface ใหม่

- 6.1 THE SYSTEM SHALL เพิ่ม `insuranceType` (`Motor` | `NonMotor`) ใน `productFilters` และ derive จาก `productGroup` ได้: CMI/VMI -> Motor, FIRE/MISC -> NonMotor
- 6.2 IF `insuranceType` ขัดแย้งกับ `productGroup` (เช่น FIRE + Motor) THEN THE SYSTEM SHALL ตอบ 400
- 6.3 IF ทั้ง `productGroup` และ `insuranceType` ว่าง THEN THE SYSTEM SHALL ตอบ 400 ("insuranceType is required when productGroup is absent") — ห้าม fan-out 2 SP (merge pagination ไม่ได้) และห้าม default เงียบ
- 6.4 WHEN filter ระบุ `insuranceType` โดยไม่มี `productGroup`, THE SYSTEM SHALL ส่ง `@ProductGroup = 'ALL'` ไป SP ฝั่งนั้น
- 6.5 THE SYSTEM SHALL เพิ่ม `countMode` (`EXACT` | `FAST`) ใน `productFilters`: absent -> EXACT; IF ค่าอื่น THEN 400 อ้าง SP error 50006 ที่ boundary (แบบเดียวกับ 50005/50007 เดิม)
- 6.6 THE SYSTEM SHALL ส่ง `@BranchCode` จาก `SpDocumentOptions.BranchCode` (server-side ตาม §1.1) โดย adapter เป็นผู้เติมตอนสร้าง SqlParameter — ไม่รับจาก client และไม่อยู่บน `SpDocumentSearchRequest`; future = actor claim (follow-up เดิมของ sp-53) (amended B2)
- 6.7 THE SYSTEM SHALL คงพฤติกรรม §2 เดิมของ `ProductFilterDto` ทุกข้อ: `saleCode` required (50005), `paymentStatus` 3 ค่า (50007), date-range 50003/50008/50009, ขนาด MaxLength เดิม

## REQ-7: ค้นสดผ่าน gateway + upsert `shop.Products`

**User Story:** ในฐานะ merchant user ฉันต้องการให้ผลค้นหามาจากระบบต้นทาง (จำลอง) สด ๆ โดยที่ cart/checkout ยังใช้เอกสารใน `shop.Products` ด้วย Guid เดิม เพื่อให้ flow ขายทั้งเส้นทำงานต่อเนื่อง

- 7.1 WHEN `GET /api/v1/products` ถูกเรียก, THE SYSTEM SHALL เรียก `ISpDocumentGateway.SearchAsync` แทนการ query `shop.Products` ตรง
- 7.2 WHEN ผลค้นหากลับมา, THE SYSTEM SHALL upsert แต่ละแถวเข้า `shop.Products` โดย key = `DocumentNo` (`IX_Products_DocumentNo` unique): ไม่มี -> `Product.Create` + `Add`; มีแล้ว -> `RefreshFromExternal` — `ProductInput` เพิ่ม `PaymentStatus` + `PaidDate` และ `Create` ต้อง honor ค่า wire (แถวใหม่ที่ upstream บอก PAID ห้ามเกิดเป็น UNPAID — มิฉะนั้น cart gate ปล่อยขายเอกสารที่จ่ายแล้ว) (amended B1)
- 7.3 THE SYSTEM SHALL มี `Product.RefreshFromExternal(ProductInput)` ที่ reuse validation ชุดเดียวกับ `Create` (private apply-fields ตัวเดียว) และ throw เมื่อ `DocumentNo` ของ input ไม่ตรงกับของ aggregate หรือ `ProductGroup` ใหม่สลับฝั่ง `InsuranceType` (Motor <-> NonMotor) (amended M9)
- 7.4 WHILE แถว local เป็น PAID, THE SYSTEM SHALL ไม่ downgrade เป็น UNPAID และคง `PaidDate` เดิม แม้ wire บอก UNPAID — field อื่นยัง update ตามปกติ
- 7.5 WHEN wire เป็น PAID พร้อม `PaidDate`, THE SYSTEM SHALL set PAID + `PaidDate` ตาม wire
- 7.6 WHEN wire เป็น PAID โดยไม่มี `PaidDate`, THE SYSTEM SHALL set PAID คง `PaidDate` เดิม และ log warning
- 7.7 IF แถว wire ขาด `DocumentNo`/`SaleCode`, `TotalPremium` เป็น NULL/<= 0 หรือ enum parse ไม่ได้ THEN THE SYSTEM SHALL ข้ามแถวนั้น + log warning โดยแถวอื่นในหน้ายังประมวลผลและตอบตามปกติ (ยอมรับว่าหน้าอาจมี item < PageSize ขณะ `TotalRows` คงเดิม)
- 7.8 IF `SaveChanges` ชน unique index (`SqlException` 2601/2627 ใน `DbUpdateException`) THEN THE SYSTEM SHALL reload แถวที่ชนแล้ว retry ทั้งชุด 1 ครั้งก่อนโยน
- 7.9 THE SYSTEM SHALL ตอบ items ตามลำดับแถวของ SP โดยแต่ละ item เป็น `ProductListItem` ที่มี Guid `Id` ของแถว local
- 7.10 THE SYSTEM SHALL map wire `SourceSystem` -> `ProductInput.ProductGroup` และ ignore wire `InsuranceType` — ฝั่ง local derive `InsuranceType` จาก `ProductGroup` เสมอ (ตาม doc ที่ล็อกไว้ใน `ProductListItem`) (F8)

## REQ-8: Response envelope ตาม §5.1

- 8.1 THE SYSTEM SHALL เปลี่ยน `GET /api/v1/products` ให้ตอบ `ProductPage`: `Items` + `TotalRows?`/`TotalPages?`/`PageNo`/`PageSize`/`HasNextPage`/`HasPreviousPage`/`CountMode`/`SearchWindowMonths` — passthrough จาก RS1 ตรง ๆ ไม่คำนวณซ้ำฝั่งเรา (breaking change — repo คุม FE เอง)
- 8.2 WHILE `countMode = FAST`, THE SYSTEM SHALL ให้ `totalRows`/`totalPages` เป็น null ใน JSON
- 8.3 THE SYSTEM SHALL คงการ parse `page`/`limit` ผ่าน `SfsQueryParser.ParsePaging` (cap 25 ตรง contract อยู่แล้ว — ไม่แตะ)
- 8.4 THE SYSTEM SHALL อัปเดต OpenAPI metadata ของ endpoint (`Produces<ProductPage>`, description, `ProducesProblem` 503)

## REQ-9: การรื้อของเดิม

- 9.1 THE SYSTEM SHALL ลบ `IProductRepository.ListAsync` + implementation + window constants + การใช้ `IClock` ใน `Persistence.MerchantRuntime/Products/ProductRepository.cs` — logic ย้ายเข้า sim SP แล้ว
- 9.2 THE SYSTEM SHALL เพิ่ม `IProductRepository.UpsertByDocumentNoAsync(IReadOnlyList<ProductInput>)` — การ upsert ทั้งก้อน (โหลด tracked, Create/Refresh, SaveChanges, retry race 1 ครั้ง) อยู่ฝั่ง Persistence เพราะ retry ต้อง reset change tracker ซึ่ง port ระดับ Application เข้าไม่ถึง (amended M7)
- 9.3 THE SYSTEM SHALL คง `Add`/`GetAsync` และไม่แตะ `GetProductByIdQuery`, `DocumentPaidOnOrderPaidConsumer`, `MarkPaid`, cart, checkout
- 9.4 THE SYSTEM SHALL ไม่มี migration/schema change บน `shop.Products` — งานนี้ไม่แตะคอลัมน์

## REQ-10: Tests

- 10.1 THE SYSTEM SHALL มี contract tests (`[Trait("Category","Integration")]`, :11433) ยิง SP จำลองตรงด้วย connection ของ `pol_app` (พิสูจน์ GRANT ไปด้วย) ครอบ: normalization defaults, cap 25, THROW ครบทั้ง 9 (assert `SqlException.Number`), ลำดับ 2 result sets + ชื่อคอลัมน์, FAST -> totals NULL + `HasNextPage` ถูก, ทะเบียนรถเจอเฉพาะ Motor, `@PaidDateFrom` บังคับ PAID, RENEWAL window
- 10.2 THE SYSTEM SHALL มี integration tests ของ adapter จริงต่อ sim DB: mapping ปกติ + error mapping (บังคับ 50006 -> `SpDocumentSearchRejectedException`)
- 10.3 THE SYSTEM SHALL มี unit tests: handler (routing, upsert, envelope passthrough, skip row) ด้วย fake gateway; `ProductFilterDto` (insuranceType/countMode); `Product.RefreshFromExternal` (no-downgrade, mismatch throw)
- 10.4 THE SYSTEM SHALL ให้ Hosts.Tests + Architecture.Tests เขียวโดยไม่แก้ boundary rule
- 10.5 THE SYSTEM SHALL มี guard test (Architecture.Tests) ยืนยัน insulation ตาม REQ-4.7: ไม่มี type `SpDocument*` โผล่ใน signature ของ `ProductPage`/`ProductListItem` และไม่มี assembly นอก `Products.Application`/`Products.Infrastructure` อ้างถึง type ใน namespace `Products.Application.Ports`

## REQ-11: คุณภาพรวม

- 11.1 WHEN งานทั้ง spec เสร็จ, `dotnet build pol-core.slnx -warnaserror` SHALL ผ่าน 0 error / 0 warning
- 11.2 WHEN งานทั้ง spec เสร็จ, test suite ทั้งหมด (รวม Integration บน :11433) SHALL เขียว ไม่มี `.only`/`.skip` ค้าง
- 11.3 WHEN งานทั้ง spec เสร็จ, `bash scripts/spec-trace.sh products-sp-gateway` SHALL พิมพ์บรรทัด `OK:`
- 11.4 WHEN งานทั้ง spec เสร็จ, `bash scripts/check-rename-identifiers.sh` และ `bash scripts/check-migration-lineage.sh` SHALL ผ่าน
- 11.5 WHEN recreate ทั้งระบบ (`docker compose down -v` + up + migrate + bootstrap), THE SYSTEM SHALL ผ่าน E2E: curl `GET /api/v1/products` ทั้งสอง `insuranceType`, `countMode` EXACT/FAST, 400 จาก countMode ผิด, add-to-cart ด้วย `Id` ที่เพิ่ง upsert

## Edge Cases & Open Questions

- **PaymentStatus แตกทาง**: เอกสารที่ local mark PAID (ผ่าน order flow ของเรา) แต่ sim DB ยังเป็น UNPAID จะยังโผล่ในผลค้น UNPAID ของ SP — eventual consistency แบบเดียวกับระบบจริง; upsert ไม่ downgrade จึงไม่เสียข้อมูล (REQ-7.4) แต่ user จะเห็นเอกสารที่ซื้อไม่ได้ในลิสต์ UNPAID จนกว่า upstream (จำลอง) จะ sync — ยอมรับ
- **แถว skip**: หน้าอาจมี item < PageSize ขณะ totals จาก SP คงเดิม (REQ-7.7) — ยอมรับ + log
- **Seed `shop.Products` 500 แถวเดิม**: หลัง cutover ลิสต์สะท้อนเฉพาะสิ่งที่ SP คืน — แถว demo เก่าที่ไม่อยู่ใน sim DB ไม่โผล่ในลิสต์ แต่ cart ที่อ้าง Id เดิมยังทำงาน; align DocumentNo = follow-up ถ้าต้องการ
- **@BranchCode filter semantics**: เอกสารไม่ระบุว่า SP จริง filter ตาม branch หรือไม่ — sim ทำ validate-only (REQ-2.11); ถ้าเจ้าของ SP ยืนยันว่า filter ต้องเพิ่ม WHERE + seed ต่อ branch
- **Non-Motor RENEWAL**: เอกสารระบุ window 2 เดือนเฉพาะ Motor — sim ให้ Non-Motor ใช้ 6 เดือนทุก DocumentType (REQ-2.5)
- **Non-Motor smart search "ใบเตือน" (§4)**: §5.2 ไม่มี field ที่ map กับ "ใบเตือน" ชัด — sim ใช้ชุดคอลัมน์เดียวกับ Motor ลบทะเบียนรถ; ต้องถามเจ้าของ SP ว่าใบเตือนค้นบนคอลัมน์ไหน (spec-architect MINOR-9)
- **Concurrent `MarkPaid` vs upsert (F3)**: search request โหลด Product (UNPAID) -> consumer mark PAID + save -> search save ทีหลัง — ยอมรับโดยไม่เพิ่มกลไก: EF update เฉพาะ property ที่เปลี่ยนจากค่าที่โหลด `RefreshFromExternal` ที่เซ็ตค่าเท่าเดิมจึงไม่ทับ PAID ของ consumer; ไม่เพิ่ม rowversion (ขัด REQ-9.4 และรอยชนแคบมาก) — ถ้าพฤติกรรม change tracker เปลี่ยนใน EF รุ่นถัดไป ต้อง revisit

### Findings log — /spec-analyze รอบ 1 (anchor: HEAD `b5a7ac6`, ไฟล์ยังไม่เคย commit; audit 2026-07-31)

| # | ประเด็น | ตัดสิน |
|---|---|---|
| F1 | REQ-2.1/4.2 นับพารามิเตอร์ §2 ผิด (17 -> จริง 18) | แก้เลขเป็น 18 |
| F2 | REQ-2 ไม่ enumerate filter predicates ของ SP | เพิ่ม REQ-2.12 ครบชุด; `@InsuredName` map บน `ShowName` |
| F3 | concurrent `MarkPaid` vs upsert save | ยอมรับ + บันทึก edge case ไม่เพิ่มกลไก |
| F4 | prod-like ยังไม่มีเจ้าภาพรัน script (migrate image อาจไม่มี sqlcmd) | requirement = รันเสร็จก่อน API รับ traffic; กลไกให้ design.md ตัดสิน |
| F5 | FAST mode "PageSize + 1" กำกวม | เขียนชัด: RS2 คืนไม่เกิน `@PageSize` แถวเสมอ |
| F6 | ตาราง sim: `SourceSystem` ซ้ำ §5.2 / `InsuranceType` ไม่ควรเป็นคอลัมน์ | ตาราง = §5.2 ยกเว้น `InsuranceType` (SP derive คงที่ต่อฝั่ง) + `BranchCode` |
| F7 | timezone ไม่ถูกประกาศ (PDF หน้า 7 เตือนตรง ๆ) | ประกาศ deviation: naive passthrough เวลาไทย ไม่แปลง UTC |
| F8 | ต้นทาง `ProductGroup` + wire `InsuranceType` ขัดแย้งไม่ถูกระบุ | เพิ่ม REQ-7.10: `SourceSystem` -> `ProductGroup`, ignore wire `InsuranceType` |

## นอกขอบเขต (จงใจ)

- **เชื่อม motordb/centerdb จริง** — วัน cutover เปลี่ยน `MotorConnectionString`/`NonMotorConnectionString` ทาง config เท่านั้น
- **actor -> sale/branch claim** (ย้าย `SaleCode`/`BranchCode` เข้า authorization context) — follow-up เดิมของ sp-53
- **เขียนสถานะ PAID กลับไป upstream** — เอกสารอ้างอิงเป็น read-only search SP; ทิศ write-back ไม่อยู่ใน contract
- **§5.1 envelope กับ list endpoint อื่น** — `ProductPage` เฉพาะ products; `PagedResult` ที่เหลือทั้ง repo คงเดิม

## Deviation ที่จงใจ (ตัดสินแล้ว)

- **ชื่อ database จำลอง = `hippodb`/`mammothdb`** ตามชื่อ server จริง ไม่ใช่ `motordb`/`centerdb` — ให้ชัดว่าเป็นตัวจำลอง (user ตัดสิน); adapter อ้าง SP ด้วยชื่อ `dbo.usp_...` โดย database มาจาก connection string จึงไม่กระทบ contract
- **`mammothdb` ตารางเดียว** แทน 3 database ภายในของ mammoth (REQ-1.3)
- **SQL error อื่นยุบเป็น 503 ตัวเดียว** — §6 แนะ 503/504; แยก timeout เป็น 504 ไม่คุ้มความซับซ้อน
- **Timezone contract = naive passthrough (F7)** — PDF หน้า 7 เตือนให้กำหนดก่อนแปลงเป็น UTC/DateTimeOffset; ตัดสิน: ทุก datetime (`StartDate`/`EndDate`/`PaidDate` + coverage params) ส่งผ่านแบบ naive (เวลาไทย) ทุกชั้น ตรงพฤติกรรม sp-53 เดิม — การย้ายไป UTC เป็นงานคนละ spec
- **sim DB รันทุก environment รวม prod-like** จนกว่าจะมี upstream จริง (user ตัดสิน) — ไม่งั้น `GET /products` ตาย
- **Deviation เดิมของ sp-53 คงทั้งหมด ห้ามรื้อ**: `SaleCode` จาก client, แคตตาล็อกกลางไม่มี `MerchantId`, `DocumentNo`/`SaleCode`/`PaymentStatus` NOT NULL ฝั่ง local, `decimal(19,2)`
