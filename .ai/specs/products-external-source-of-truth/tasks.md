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

- [x] 2. **ตัวตรวจเอกสารที่ขายแล้ว** — พอร์ต `IDocumentSaleProbe` + `DocumentKey`/`DocumentSaleState`/
     `DocumentSaleStatus` ใน `BuildingBlocks.Application`, adapter LINQ + `IgnoreQueryFilters()` ใน
     `Persistence.MerchantRuntime` (join `shop.Orders`, subquery `txn.PaymentSessions` ด้วยเงื่อนไขเวลา
     `now - Session.OpenTtl` และรวม `SessionStatus.Paid`), จับคู่ `ProductGroup` ในหน่วยความจำ,
     migration เพิ่ม `IX_OrderItems_DocumentNo (DocumentNo) INCLUDE (OrderId, ProductGroup)`,
     ขึ้น allowlist ของ `BypassPrimitiveTests` พร้อมเหตุผลว่าทำไมไม่ยิง `ISecurityTelemetry` +
     ยืนยันว่าคอลัมน์ `DocumentNo` ทุกตารางใช้ collation เดียวกัน ยังไม่มี caller ในขั้นนี้
     Satisfies: REQ-5.1, REQ-5.2, REQ-5.10, REQ-5.11, REQ-5.12, REQ-5.13, REQ-5.14, REQ-5.15, REQ-2.7.

- [x] 3. **อ่านเอกสารรายใบสดจากต้นทาง** — `ISpDocumentGateway.LookupAsync` + `SpDocumentLookupRequest` +
     `SpDocumentAmbiguousException : ArgumentException` + `DocumentView` (DTO กลางของเอกสารหนึ่งใบ) +
     `LookupDocumentQuery`/`Handler` ที่ยิง SP ด้วย `@SearchText`, `@PaymentStatus = 'ALL'`,
     `@CountMode = 'FAST'` แล้วกรองแถวที่ `DocumentNo` ตรงตามกฎ normalize, ปฏิเสธ `documentNo`
     ยาวเกิน 100 ที่ขอบ (ขีดของ `@SearchText`) และเกิน 150/ว่าง ตามสัญญา ไม่ map เป็น route
     Satisfies: REQ-3 (ทุกข้อ), REQ-2.5.

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

- [x] 5. **รื้อข้อมูล demo ให้ยืนบนต้นทางจริง** — `docker/bootstrap/seed-demo.sql` เลิกสร้างและเลิกอ่านกลับ
     จาก `shop.Products`, ตั้ง `SaleCode` ของ merchant user เป็นรหัสที่มีจริงในต้นทาง, แถว cart/order
     ทุกแถวพก `DocumentNo` ที่ sim การันตีว่าออกจริง, verify query ท้ายไฟล์ที่นับ `shop.Products`
     เปลี่ยนไปนับอย่างอื่น
     Satisfies: REQ-6.9, REQ-6.10.
- ไม่มี `Batch:` tag — ทั้งห้า task เป็นคนละโดเมนกัน ไม่เข้าเกณฑ์ batching
