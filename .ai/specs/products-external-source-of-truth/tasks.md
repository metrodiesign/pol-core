# Implementation Tasks: Products อ่านสดจากฐานข้อมูลภายนอก

> Status: approved 2026-08-05

> แต่ละ task เป็นชิ้นงานที่พิสูจน์ได้ด้วยตัวเอง ทำให้จบในรอบเดียว (แตะหลายไฟล์ได้)
> แตกขั้นตอนย่อยเองตอนลงมือ — ห้ามแตกไว้ล่วงหน้าในไฟล์นี้

> **ลำดับสำคัญ** task 1-3 สร้างชิ้นส่วนใหม่ทั้งหมดโดยยังไม่มีใครเรียกใช้ ทำให้ repo เขียวได้ทุกขั้น
> จากนั้น task 4 คือการสับสวิตช์ครั้งเดียว — ตัวใหญ่ที่สุดและแยกย่อยกว่านี้ไม่ได้ เพราะ REQ-6.1
> บังคับให้การเปลี่ยนคอลัมน์ตัวระบุกับการ DROP ตารางอยู่ใน migration เดียวกัน และเส้นทางซื้อทั้งสาย
> อ้าง `ProductId` ผ่าน CLR type ที่หายไปพร้อมกัน ถ้าแยกจะเหลือ repo ที่ compile ไม่ผ่านคาไว้ระหว่างทาง

- [x] 1. **เปลี่ยน `ProducerCode` เป็น `SaleCode` แล้วให้ server เป็นคนกำหนดรหัสผู้ขาย** — rename ฟิลด์ตลอดสาย
     (`Merchants.Domain.Users.User`, `RegistrationAttempt`, `SubmitRegistration`, `GetRegistrationHistory`,
     `Resolution`/`AccountSnapshot`, EF config คู่กระจก, wire ของฟอร์มสมัคร + ประวัติการสมัคร,
     `docs/reference/merchants.md`) + migration `RenameColumn` สองตารางแบบไม่ทำข้อมูลหาย + validation
     20 ตัวอักษร/ASCII ที่ `User.SetDetails` + claim `sale_code` ใน `UserSessionAuthenticationHandler` +
     `IActorContext.SaleCode` แบบ default interface member + guard กันค่าที่ถูกตัดก่อนผูกพารามิเตอร์
     Satisfies: REQ-10 (ทุกข้อ), REQ-4.8, REQ-4.9, REQ-4.10, REQ-4.11.
     Verify: `dotnet test` — test ยึดชื่อ wire ทั้งสองทาง (ส่ง `saleCode` ติด / ส่ง `producerCode` ไม่ติด),
     integration test ว่าค่าเดิมยังอยู่ครบหลัง migrate, unit test ความยาว 21 ตัวและอักษรไทยถูกปฏิเสธ
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 64 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx` (มี `.env.integration` โหลด, SQL Server จริง :11433) -> 17 project, 1790 test ผ่านหมด, EXIT=0 (รวม `SaleCodeRenameMigration` บน SQL จริง, `UserRegistrationFormTests` wire 2 ทาง, `MerchantUserTests` 21 ตัว/อักษรไทยถูกปฏิเสธ, `SaleCodeRenameCompletenessTests`)
       - test: `bash scripts/check-rename-identifiers.sh` -> OK · `bash scripts/check-migration-lineage.sh` -> OK · `dotnet ef migrations has-pending-model-changes` -> No changes
       - viewports: n/a — logic-only
       - deviations: REQ-4.9 ส่งมอบเฉพาะ plumbing (claim + `IActorContext.SaleCode` null-vs-value) ส่วน 403 จริงที่ endpoint เป็นของ task 4 ตาม `tasks.md` เอง; integration test สำหรับ REQ-10.2 render SQL จาก migration ตัวจริงแล้วรันบน SQL Server จริงแทน harness ที่รันทั้งสาย (repo ไม่มี harness นั้น) — audit/verify/review ยอมรับแล้ว

- [x] 2. **ตัวตรวจเอกสารที่ขายแล้ว** — พอร์ต `IDocumentSaleProbe` + `DocumentKey`/`DocumentSaleState`/
     `DocumentSaleStatus` ใน `BuildingBlocks.Application`, adapter LINQ + `IgnoreQueryFilters()` ใน
     `Persistence.MerchantRuntime` (join `shop.Orders`, subquery `txn.PaymentSessions` ด้วยเงื่อนไขเวลา
     `now - Session.OpenTtl` และรวม `SessionStatus.Paid`), จับคู่ `ProductGroup` ในหน่วยความจำ,
     migration เพิ่ม `IX_OrderItems_DocumentNo (DocumentNo) INCLUDE (OrderId, ProductGroup)`,
     ขึ้น allowlist ของ `BypassPrimitiveTests` พร้อมเหตุผลว่าทำไมไม่ยิง `ISecurityTelemetry` +
     ยืนยันว่าคอลัมน์ `DocumentNo` ทุกตารางใช้ collation เดียวกัน ยังไม่มี caller ในขั้นนี้
     Satisfies: REQ-5.1, REQ-5.2, REQ-5.10, REQ-5.11, REQ-5.12, REQ-5.13, REQ-5.14, REQ-5.15, REQ-2.7.
     Depends on: —.
     Verify: integration test บน SQL Server จริง — order `Paid` -> `Sold`, `AwaitingPayment` ไม่มี session
     -> `Sellable`, session `Created` ในอายุ -> `PaymentInFlight` และเลย TTL -> `Sellable` โดยไม่แก้แถว,
     session `Paid` แต่ order ยังไม่ `Paid` -> `PaymentInFlight`, 25 key ยิง command เดียว, เห็นข้าม merchant
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 64 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx` (มี `.env.integration` โหลด, SQL Server จริง :11433) -> 17 project, 1804 test ผ่านหมด, EXIT=0 (รวม integration 13 ตัวของ task 2: `DocumentSaleProbe`/`DocumentNoCollation` — order Paid->Sold ข้าม merchant, session ในอายุ/เลย TTL โดยไม่แก้แถว, session Paid แต่ order ยังไม่ Paid, 25 key ยิง command เดียว, collation CI เดียวกันทุกคอลัมน์ DocumentNo, trim หัวท้าย + soft-hyphen)
       - test: `bash scripts/check-rename-identifiers.sh` -> OK · `bash scripts/check-migration-lineage.sh` -> OK · `dotnet ef migrations has-pending-model-changes` -> No changes
       - viewports: n/a — logic-only
       - deviations: ยังไม่มี caller (REQ-5.3-5.9/5.16 เป็นของ task 4 ตาม `tasks.md` เอง); HIGH ของ audit รอบ 2 (fullwidth ทำให้ `InvariantCultureIgnoreCase` เข้มกว่า `Thai_100_CI_AS`) ไม่ใช่การละเมิด REQ-2.3 เพราะ REQ-2.3 บังคับแค่ trim หัวท้าย + ไม่สนตัวพิมพ์ ไม่บังคับ width-folding — verify/review ยอมรับ, orchestrator สั่งปิดบัญชี task นี้

- [x] 3. **อ่านเอกสารรายใบสดจากต้นทาง** — `ISpDocumentGateway.LookupAsync` + `SpDocumentLookupRequest` +
     `SpDocumentAmbiguousException : ArgumentException` + `DocumentView` (DTO กลางของเอกสารหนึ่งใบ) +
     `LookupDocumentQuery`/`Handler` ที่ยิง SP ด้วย `@SearchText`, `@PaymentStatus = 'ALL'`,
     `@CountMode = 'FAST'` แล้วกรองแถวที่ `DocumentNo` ตรงตามกฎ normalize, ปฏิเสธ `documentNo`
     ยาวเกิน 100 ที่ขอบ (ขีดของ `@SearchText`) และเกิน 150/ว่าง ตามสัญญา ไม่ map เป็น route
     Satisfies: REQ-3 (ทุกข้อ), REQ-2.5.
     Depends on: —.
     Verify: unit test บน gateway fake (ไม่พบ -> null, ตรงเป๊ะสองแถว -> throw, ต่างช่องว่างหัวท้าย -> พบ,
     ใช้ `productGroup` จากแถวที่ต้นทางคืน, 101 ตัวอักษร -> 400) + integration test ยิง SP จริงด้วยเลข
     เอกสารที่มี `/` และอักษรไทย
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 64 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx` (unit/non-integration ครบ 16 project) -> 1677 test ผ่านหมด, EXIT=0 (รวม `LookupDocumentHandlerTests` 18 ตัว: ไม่พบ -> null, สองแถวตรง -> throw, trim หัวท้ายแล้วพบ, productGroup จากแถวต้นทาง, blank/>150/>100 -> 400)
       - test: `set -a; source .env.integration; set +a; dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"` (SQL Server จริง :11433) -> 150 test ผ่านหมด รวม `SpDocumentGatewayIntegrationTests` 21/21 (lookup เลข `/`+อักษรไทย Motor/NonMotor, `A_lookup_is_unfiltered_by_status_and_group` พิสูจน์ `@PaymentStatus='ALL'`+`@ProductGroup='ALL'` พร้อม mutation-verified รอบ 2)
       - test: `bash scripts/check-rename-identifiers.sh` -> OK · `bash scripts/check-migration-lineage.sh` -> OK · `dotnet ef migrations has-pending-model-changes` -> No changes · `git status` ยืนยันไม่มี migration ใหม่ของ task 3
       - viewports: n/a — logic-only
       - deviations: REQ-3 พิสูจน์เต็มเมื่อ task 4 ต่อ caller (add-item/checkout); task 3 ส่งเฉพาะชิ้นส่วน + unit/integration ของชิ้นส่วนนั้นตาม `tasks.md` เอง (ยังไม่มี caller). MEDIUM ของ audit (LookupAsync ไม่ trim/ไม่ guard ความยาวที่ adapter — guard อยู่ที่ handler ล้วน, page 1/25 ทิ้ง HasNextPage) ไม่ละเมิด AC ที่มีวันนี้เพราะ caller เดียว (handler) trim+guard ให้ก่อนเสมอ — audit/verify/review ยอมรับ, ต้องปิดก่อน task 4 ต่อสาย

- [x] 4. **สับสวิตช์: `DocumentNo` เป็นตัวระบุ ปลดระวังแคตตาล็อกสำเนา และปิดเส้นทางซื้อบนรูปใหม่** —
     migration เดียว 11 ขั้น (rename ที่เหลือ -> เพิ่มคอลัมน์ `shop.CartItems` -> backfill จาก
     `shop.Products` -> ลบแถวที่ join ไม่เจอ -> NOT NULL -> drop `ProductId` สามตาราง -> DROP TABLE)
     พร้อม `Down()` ที่คืนโครงสร้างครบโดยไม่คืนข้อมูล · `Carts`/`Checkouts`/`Orders` domain + EF config
     คู่กระจก 6 ไฟล์ + read model ทุกตัว (`GetOrders`, `GetOrderDetail`, `OrderSummaryReader` raw SQL,
     `CheckoutConfirmedItem`) เปลี่ยนจาก `ProductId` เป็น `DocumentNo` · `ListProductsHandler` เลิกใช้
     `IProductRepository` ใช้ `DocumentView` + probe แทน, ตัดฟิลด์ `id`, เพิ่ม `soldByPlatform`, ตัด
     member `SaleCode` ออกจาก `ProductFilterDto` และเลิกบังคับ `productFilters` โดยเช็ค 403 ก่อน parse ·
     add-item รับ `documentNo`+`productGroup` แล้ว lookup สดเพื่อได้ราคา, gate ต้นทาง PAID และ probe,
     `Cart.AddItem` ปฏิเสธเอกสารซ้ำ, route ลบ/แก้จำนวนใช้ `itemId` · checkout อ่านสดต่อบรรทัดเป็น
     snapshot โดยราคายังมาจากตะกร้า · `CreateSessionHandler` เรียก probe ก่อน mint charge ·
     `IDoubleSellAuditor` + adapter แทน consumer เดิม, ลบ `Product`/`ProductInput`/repository/
     `CreateProductCommand`/`GetProductByIdQuery`/`DocumentPaidOnOrderPaidConsumer`/`Contracts.OrderPaid`
     + จุด enqueue + entry ใน `EventTypes` · ย้าย anchor ของ `Architecture.Tests` 5 ไฟล์ + ถอด entity
     `Product` ออกจาก write authorizer + ปรับ `assert-fresh-db.sql`
     Satisfies: REQ-1 (ทุกข้อ), REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.6, REQ-4.1, REQ-4.2, REQ-4.3,
     REQ-4.4, REQ-4.5, REQ-4.6, REQ-4.7, REQ-5.3, REQ-5.4, REQ-5.5, REQ-5.6, REQ-5.7, REQ-5.8, REQ-5.9,
     REQ-5.16, REQ-6.1, REQ-6.2, REQ-6.3, REQ-6.4, REQ-6.5, REQ-6.6, REQ-6.7, REQ-6.8, REQ-7 (ทุกข้อ),
     REQ-8 (ทุกข้อ), REQ-9 (ทุกข้อ).
     Depends on: 1, 2, 3.
     Verify: `dotnet build -warnaserror` + `dotnet test` ทั้งชุด — endpoint test ว่า `GET /products` ไม่เกิด
     `SaveChanges`, ไม่มีฟิลด์ `id`, เมิน `saleCode` จาก client, actor ไม่มี `SaleCode` -> 403 มาก่อน 400;
     add-item ตั้งราคาจากต้นทาง, ต้นทาง PAID -> 400 และ checkout -> 409, เอกสารที่ขายแล้ว -> 400/409/409
     ทั้งสามด่าน, ข้อความ 409 ไม่มี order id หรือรหัส merchant, `itemId` route ทำงาน, เอกสารซ้ำ -> 400,
     `documentNo` ซ้ำใน `insuredPersons` -> 400, ต้นทางล่ม -> 503, `GET /orders/{token}/summary` คืน
     `documentNo`; integration test ว่า cart เดิมได้ `DocumentNo` ครบและแถวที่ join ไม่เจอถูกลบ, `Down()`
     คืนโครงสร้างตรงกับ snapshot ก่อนหน้า
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 64 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> 16 project, 1619 test ผ่านหมด, EXIT=0 (รวม `MerchantLifecycleEndpointTests` add-item success ราคาจาก TotalPremium/sold 400/checkout sold 409/null 409/PAID 409/dup insured documentNo 400/403-no-saleCode, `CreateSessionHandlerTests` 27 รวม pre-charge sold-check REQ-5.6 + message ไม่หลุด order/merchant REQ-5.7, `ListProductsHandlerTests` 25 รวม dedup DocumentNo REQ-1.7, `SfsOpenApiTests` 6 รวม productFilters required + ไม่โฆษณา saleCode REQ-4.8/3.2)
       - test: integration บน SQL Server จริง :11433 -> 145/148 ผ่าน รวม `DropProductCatalogueMigrationIntegrationTests` 2/2 (backfill cart line + drop orphan REQ-6.2/6.3, Down คืนโครงสร้างไม่คืนข้อมูล REQ-6.8), `DoubleSellAuditorIntegrationTests` 3/3 (REQ-5.16 cross-merchant read + carve-out own-order); 3 fail = `SpDocumentContractTests` date-drift ของ sim seed (pre-existing ยืนยันอิสระด้วย git diff ว่าง + docker inspect + date ต่างวัน) ไม่ใช่ regression และไม่อยู่ใน Satisfies ของ task 4
       - test: schema จริงหลัง migrate (sqlcmd) -> `shop.Products` 0 table, `CartItems`/`OrderItems`/`CheckoutItems.ProductId` 0 คอลัมน์, `CartItems.DocumentNo` NOT NULL · `bash scripts/check-rename-identifiers.sh` -> OK · `bash scripts/check-migration-lineage.sh` -> OK · `dotnet ef migrations has-pending-model-changes` -> No changes
       - viewports: n/a — logic-only
       - deviations: `productFilters` ยังบังคับจริงที่ endpoint (ResolveTarget คืน 400 เมื่อไม่ระบุทั้ง productGroup และ insuranceType) ขัดถ้อยคำ "ไม่บังคับ" ใน design.md:336/351/531/626 + tasks.md:73 แต่ยึด REQ-3.2 (Motor/NonMotor เป็นคนละ stored procedure ไม่มี default side) — บันทึกเต็มใน changes-t4.md:245-256; REQ-8.6 order summary `documentNo` PASS ด้วย compile-time chain + SQL query readable ไม่มี test assert ค่าใน JSON ตรง ๆ (คงพฤติกรรมเดิม); F1 docs/reference (merchants.md/products.md ฯลฯ) ยังพูดผิดเรื่อง saleCode/upsert = นอก scope task 4 (ไม่มีงาน docs สั่งใน tasks.md) มอบให้ task 5/follow-up

- [x] 5. **รื้อข้อมูล demo ให้ยืนบนต้นทางจริง** — `docker/bootstrap/seed-demo.sql` เลิกสร้างและเลิกอ่านกลับ
     จาก `shop.Products`, ตั้ง `SaleCode` ของ merchant user เป็นรหัสที่มีจริงในต้นทาง, แถว cart/order
     ทุกแถวพก `DocumentNo` ที่ sim การันตีว่าออกจริง, verify query ท้ายไฟล์ที่นับ `shop.Products`
     เปลี่ยนไปนับอย่างอื่น
     Satisfies: REQ-6.9, REQ-6.10.
     Depends on: 4.
     Verify: รัน bootstrap ใหม่ทั้งกอง (`docker compose down -v` แล้วขึ้นใหม่) แล้วล็อกอินด้วย merchant
     user ที่ seed ไว้ เรียก `GET /products` ต้องได้แถวไม่ว่าง และกด checkout จากแถวที่ได้ต้องไม่ 409
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 64 projects, 0 errors, 0 warnings
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> 16 project, 1619 test ผ่านหมด, EXIT=0
       - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` (SQL Server จริง :11433 + hippodb/mammothdb/VCentralPay จริง) -> `SeedDemoIntegrationTests` 2/2 + `MerchantCatalogueLiveEndpointTests` 1/1 (HTTP จริงผ่าน WebApplicationFactory, real SpDocumentGateway/DocumentSaleProbe, รัน seed-demo.sql จริง: GET /products ไม่ว่าง REQ-6.10, add-item+checkout เอกสาร seed จริง 200 ไม่ 409 REQ-6.9); 12 fail = `SpDocumentContractTests` date-drift ของ sim seed (pre-existing, git diff ว่างเปล่าเทียบ base 2370904 + docker inspect container ข้ามเที่ยงคืน) ไม่ใช่ regression และนอก Satisfies ของ task 5
       - test: `bash scripts/seed-demo.sh` (POL_SQL_SERVER=localhost,11433 POL_DB=VCentralPay) -> `seed-demo: OK.` EXIT=0 รัน 2 ครั้งติด ตัวเลขเดิมทุกตาราง (พิสูจน์ idempotent); `grep shop.Products docker/bootstrap/seed-demo.sql` -> ว่างเปล่า (ไม่มี DML/DDL จริงอ้าง shop.Products เหลือแค่คอมเมนต์); ยิง `usp_Motor_SearchDocument` เองครบ 6 SaleCode (77001-77006) บน hippodb คืนแถวไม่ว่างทุกรหัส (24-41 แถว)
       - test: `bash scripts/check-rename-identifiers.sh` -> OK · `bash scripts/check-migration-lineage.sh` -> OK · `dotnet ef migrations has-pending-model-changes` -> No changes · `bash scripts/spec-trace.sh products-external-source-of-truth` -> OK (85 เกณฑ์อ้างครบ, EARS lint ผ่าน)
       - viewports: n/a — logic-only
       - deviations: `Verify:` line (bootstrap `docker compose down -v` เต็มรูป + real Google OIDC login) รันตามตัวอักษร 100% ไม่ได้ในสภาพแวดล้อม dev นี้ (ไม่มี Google OAuth credential ใน `.env*` เลย) — แทนด้วย `MerchantCatalogueLiveEndpointTests` (HTTP จริง+real gateway/probe/seed, fake เฉพาะ auth scheme ตาม convention เดียวกับ endpoint test อื่น) + พิสูจน์ seed idempotent ด้วยการรันสคริปต์ตรง 2 ครั้ง; audit/verify/review ยอมรับแล้ว. nit ไม่บล็อก (audit F9): checkout ในเทสต์ใช้เอกสารที่เลือกล่วงหน้า (พิสูจน์ sellable อิสระแล้ว) ไม่ได้ parse แถวจาก response ของ GET /products มาต่อ — เป็นจุดอ่อนการออกแบบเทสต์ ไม่ใช่ bug ฟังก์ชัน

## Suggested execution batches

งานนี้ **coupled สูง** — task 4 ใช้ทุกอย่างที่ 1-3 สร้าง และแตะโมดูลเดียวกันซ้ำ ๆ

- **แนะนำ:** รันทั้งหมดใน session เดียว — `scripts/pane-loop.sh products-external-source-of-truth all-in-one`
  (หรือ `/spec-implement all`) session แยกไม่แชร์ cache จึงต้องจ่ายค่าอ่าน context ใหม่ทุกครั้ง
- **ทางเลือกเพื่อความแม่นยำ:** แยก task 4 ออกเป็น session ของตัวเอง (`/spec-implement 4`) เพราะเป็น
  cutover บนเส้นทางเงิน — กัน long-context drift แลกกับค่า cache ที่ต้องจ่ายใหม่หนึ่งรอบ
- task 1, 2, 3 ไม่พึ่งกันเลย รันขนานได้ถ้าต้องการ แต่ทั้งสามตัวเล็กพอที่จะอยู่ session เดียวกัน
- ไม่มี `Batch:` tag — ทั้งห้า task เป็นคนละโดเมนกัน ไม่เข้าเกณฑ์ batching
