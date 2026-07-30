# Design: checkout-chain-document-fields

> Status: approved-for-implementation 2026-07-30
> อ่าน `requirements.md` ก่อน. งานนี้ทำบน branch `feat/products-insurance-document` เข้า PR #143 เดิม (user ยืนยัน)

## หลักการ

1 การเปลี่ยนแปลงเดียวไหลตามเส้น snapshot: `ProductView` -> `CheckoutItemInput` -> `Checkouts.Item` -> `CheckoutConfirmedItem` (event) -> `OrderItemInput` -> `Orders.Item` -> read models -> DB columns. ทุก record เป็น positional — เปลี่ยน signature แล้ว compiler พาไปครบทุก call site เอง

## Field mapping (ชุดเดียว ใช้ทุกชั้น)

| เดิม (ลบ) | ใหม่ (เพิ่ม) | CLR | Column |
|---|---|---|---|
| `Money SumInsured` | `string DocumentNo` | required, trim | `DocumentNo` nvarchar(150) NOT NULL |
| `int CoverageDurationDays` | `string ProductGroup` | required, trim, wire value ("CMI"/"VMI"/"FIRE"/"MISC") | `ProductGroup` varchar(10) NOT NULL |
| `string Insurer` | `string DocumentType` | required, trim, wire value ("APPLICATION"/"POLICY"/"RENEWAL"/"ENDORSEMENT") | `DocumentType` varchar(20) NOT NULL |
| — | `string? PolicyNumber` | nullable | `PolicyNumber` varchar(150) NULL |
| — | `DateTime? StartDate` | nullable | `StartDate` datetime2(0) NULL |
| — | `DateTime? EndDate` | nullable | `EndDate` datetime2(0) NULL |

ลำดับ param ใหม่ใน record/ctor (ยึดตลอด chain ให้เหมือนกัน):
`ProductId, Quantity, UnitPrice, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth`

### Rationale

- **string ไม่ใช่ enum**: Contracts/Checkouts/Orders ห้าม reference `Products.Domain` ข้ามโมดูล — snapshot คือค่าคงที่ ณ เวลาซื้อ เก็บ wire value ตรง ๆ (ตรง column ที่ Products ก็เก็บเป็น string อยู่แล้วผ่าน enum→string conversion)
- **StartDate/EndDate แทน CoverageDurationDays**: coverage window จริงของเอกสาร ไม่ derive
- **ไม่มี SumInsured/Insurer แทนที่**: SP contract ไม่ส่งทุนประกัน/ชื่อบริษัทรับประกันตรง ๆ มา — ห้าม fabricate
- **ไม่ re-GRANT ใน migration**: alter column บนตารางเดิม ไม่ drop table (บทเรียน PR #143: DROP TABLE ลบ grant และ pol_admin ไม่มีอยู่แล้วหลัง #112)

## จุดแก้ตามชั้น (เส้นทางไฟล์จริง ยืนยันจากการสำรวจ 2026-07-30)

1. **Contracts**: `src/Contracts/CheckoutConfirmed.cs` — `CheckoutConfirmedItem` positional ใหม่
2. **Checkouts**: `Checkouts.Domain/Items/CheckoutItemInput.cs`, `Items/Item.cs` (property + ctor + invariant ใหม่ ตาม pattern PII เดิม :51-56), `Session.cs:64-69` (ส่งผ่าน), `Checkouts.Application/ConfirmCheckout.cs:42-49` (mapper)
3. **Orders**: `Orders.Domain/Items/Item.cs`, `Items/OrderItemInput.cs`, `Order.cs:118-122` (ส่งผ่าน — invariant Order.Create เดิมแตะแค่ UnitPrice ไม่ต้องแก้), `Orders.Application/CheckoutConfirmedConsumer.cs:44-49`, `GetOrderDetail.cs:19-21,51-53`, `GetOrders.cs:14-16,35-37`
4. **EF dual-config 4 ไฟล์** (คู่ต่อโมดูล ต้อง identical):
   - `Checkouts.Infrastructure/Items/ItemConfiguration.cs` + `Persistence.MerchantRuntime/Checkouts/Items/ItemConfiguration.cs`
   - `Orders.Infrastructure/Items/ItemConfiguration.cs` + `Persistence.MerchantRuntime/Orders/Items/ItemConfiguration.cs`
   - แบบ: `Property(x => x.DocumentNo).HasMaxLength(150).IsRequired();` / `ProductGroup` `.HasMaxLength(10).IsUnicode(false).IsRequired()` / `DocumentType` `.HasMaxLength(20).IsUnicode(false).IsRequired()` / `PolicyNumber` `.HasMaxLength(150).IsUnicode(false)` / `StartDate`/`EndDate` `.HasPrecision(0)` — ลบ block ComplexProperty SumInsured + CoverageDurationDays + Insurer("InsurerName")
5. **Host**: `Program.cs:770-780` snapshot ใหม่ (`product.DocumentNo, product.ProductGroup.ToString(), product.DocumentType.ToString(), product.PolicyNumber, product.StartDate, product.EndDate`); cart endpoint `:667-668` `product.Price` -> `product.TotalPremium`; ลบ bridge 4 ตัวใน `Products.Application/ProductView.cs:47-60`
6. **Migration**: `dotnet ef migrations add CheckoutChainDocumentFields --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api` (ต้องมี `POL_DESIGN_SQL` จาก `.env`) — scaffold alter อัตโนมัติจาก model; NOT NULL columns ได้ `defaultValue: ""` สำหรับแถว dev เก่า (ยอมรับ pre-prod)
7. **Seed**: `docker/bootstrap/seed-demo.sql` INSERT `shop.OrderItems` (~:389) — column ใหม่, ใส่ DocumentNo อ้าง product แถว e9 (เช่น N'00098-69100/กธ/900001-10'), ProductGroup/DocumentType ตรง products ที่อ้าง
8. **Raw-SQL tests**: `Integration.Tests/OrderSummaryReaderIntegrationTests.cs:39`, `Integration.Tests/OrderItemPolicyGrantsTests.cs:32` — INSERT column list ใหม่

## Traps (อ่านก่อนเริ่มทุก task)

1. **task gate**: flip `- [ ]` -> `- [x]` และเขียน `Evidence:` ใน **Edit เดียวกัน**; บรรทัด `Evidence:` ห้ามมี `-` นำหน้า
2. **dual-config**: แก้ config ต้องแก้คู่ (migration-owner + Persistence.MerchantRuntime) — `EntitySchemaMappingTests` จับได้ถ้าหลุด
3. **column ชื่อไม่ตรง CLR เดิม**: `Insurer` -> column `InsurerName` — ตอนลบให้ลบทั้งคู่, ตอน grep หา residue ต้อง grep ทั้งสองชื่อ
4. **migration timestamp** ต้องใหม่กว่า `20260730072057` (ให้ `dotnet ef migrations add` gen เอง ห้าม hand-write timestamp)
5. **Integration tests**: `source .env.integration` ใน Bash call เดียวกับ `dotnet test` (DB :11433 = pol-db container ของ repo นี้)
6. **hook block compound command**: ถ้า `git add && git commit` โดน block — คำสั่งทั้งก้อนตาย ต้องเช็คว่า part ไหนรันไปแล้ว; commit แล้วต้องตรวจ committed tree ว่ามีไฟล์ครบ (`git show --stat HEAD`)
7. **ห้าม push develop / ห้าม force push** — งานทั้งหมดอยู่บน `feat/products-insurance-document`
8. `.env*` ไฟล์: Read/Edit และ Bash file utils โดน deny — ใช้ `git` subcommand หรือ `source .env` ใน Bash เท่านั้น
9. Program.cs มี using alias `DocumentType = Products.Domain.DocumentType` อยู่แล้ว (กัน Scalar.AspNetCore ชน) — อย่าลบ

## Requirement Traceability

| เกณฑ์ | จุดใน design |
|---|---|
| REQ-1 (1.1, 1.2, 1.3, 1.4) | ตาราง Field mapping + ลำดับ param กลาง + invariant (หัวข้อ "Field mapping") และจุดแก้ชั้น 1-3, 5 |
| REQ-2 (2.1, 2.2) | จุดแก้ชั้น 1 (Contracts) + ชั้น 3 (`CheckoutConfirmedConsumer` map 1:1) |
| REQ-3 (3.1, 3.2, 3.3) | จุดแก้ชั้น 5 (Host: ถอด bridge บน `ProductView`, cart ใช้ `TotalPremium`) |
| REQ-4 (4.1, 4.2) | จุดแก้ชั้น 3 (read models `GetOrderDetail`/`GetOrders` — คง reveal-audit/masking) |
| REQ-5 (5.1, 5.2, 5.3) | จุดแก้ชั้น 4 (EF dual-config 4 ไฟล์), ชั้น 6 (migration), ชั้น 7 (seed) |
| REQ-6 (6.1, 6.2, 6.3) | หัวข้อ Traps + เกณฑ์ Done ของ tasks.md task 3/5 (full gate) |
| REQ-7 (7.1, 7.2, 7.3, 7.4) | หัวข้อ "REQ-7: mark เอกสารเป็น PAID" (task 6) |

## REQ-7: mark เอกสารเป็น PAID เมื่อ order จ่ายสำเร็จ (task 6 — เพิ่มจาก Codex F2)

Codex review PR #143 F2 (P1): `Product.MarkPaid` ไม่มี production caller — order จ่ายแล้วเอกสารค้าง `UNPAID`/active ขายซ้ำได้ (double-sell). User ยืนยันให้ทำต่อใน PR นี้

- 7.1 WHEN order transition -> Paid สำเร็จ (`Order.MarkPaid` คืน true ใน `OrderPaidConsumer`), THE SYSTEM SHALL enqueue integration event `Contracts.OrderPaid` (พก `MerchantId`, `ProductIds` ของทุก order line, `OccurredAt`) ในทรานแซกชันเดียวกับการ save order (pattern `CheckoutConfirmedConsumer` -> `IOutbox.Enqueue`)
- 7.2 WHEN `OrderPaid` ถูก consume โดย Products, THE SYSTEM SHALL โหลดแต่ละ product แล้วเรียก `Product.MarkPaid(OccurredAt)` (set `PAID` + `IsActive=false`) แล้ว save — idempotent ต่อ replay (MarkPaid set state ทับได้)
- 7.3 THE SYSTEM SHALL อนุญาต `Update` บน `Product` ใน background-dispatch scope (`WorkerWriteAuthorizer`) — มิฉะนั้น consumer โดน write floor block
- 7.4 THE SYSTEM SHALL ลงทะเบียน `OrderPaid` ใน `OutboxDispatcher` event-type dictionary (มิฉะนั้น dispatch throw "No outbox publisher registered")

Design (Option A — ตรง outbox pattern ที่ repo ใช้):
- `src/Contracts/OrderPaid.cs` — `sealed record OrderPaid(Guid MerchantId, IReadOnlyList<Guid> ProductIds, DateTime OccurredAt) : INotification` (+ `SchemaVersion`)
- `Orders.Application/OrderPaidConsumer.cs` — inject `IOutbox` เพิ่ม; หลัง `MarkPaid` true, enqueue `OrderPaid(order.MerchantId, order.Items.Select(i => i.ProductId).ToList(), notification.OccurredAt)` ก่อน `SaveChangesAsync`. **ตรวจ `OrderRepository.GetAsync` ว่า `.Include(o => o.Items)` — ถ้าไม่ include ProductIds จะว่าง; ถ้าต้อง add Include ให้เช็ค caller อื่นก่อน (perf)**
- Products consumer ใหม่ (ตัวแรกของ module) — `Products.Application/DocumentPaidOnOrderPaidConsumer.cs` (ชื่อเลี่ยงชนกับ `Orders.Application.OrderPaidConsumer`): `INotificationHandler<Contracts.OrderPaid>`, inject `IProductRepository`+`IUnitOfWork`, loop `GetAsync` -> `MarkPaid` -> `SaveChangesAsync`; product ที่หาไม่เจอ/merchant อื่น = skip (defensive, at-least-once). `Products.Application.csproj` อ้าง Contracts อยู่แล้ว ไม่ต้องแก้ csproj/registration (Mediator auto-discover)
- `Persistence.MerchantRuntime/Outbox/OutboxDispatcher.cs` — add `[nameof(Contracts.OrderPaid)] = typeof(Contracts.OrderPaid)` ใน EventTypes
- `Hosts/Api/BackgroundDispatch/WorkerWriteAuthorizer.cs` — ให้ `Update` ครอบ `Product` (เหมือนที่ครอบ `Order`)

Traps เพิ่ม: (a) ชื่อ consumer ห้ามชนกับ `Orders.Application.OrderPaidConsumer`; (b) idempotent — Orders' consumer ยิง `OrderPaid` เฉพาะตอน `MarkPaid` คืน true อยู่แล้ว (replay = no-op) จึงไม่ยิงซ้ำ; (c) E2E `InsuranceCheckoutEndToEndTests.CreatePaidOrderAsync` ปัจจุบัน mark paid โดยเรียก `order.MarkPaid` ตรง (ข้าม consumer) — ต้องปรับให้ผ่าน consumer จริง หรือเพิ่ม step ยิง Products consumer + assert `Product.PaymentStatus == PAID` && `!IsActive`

## Follow-up ที่บันทึกไว้ (ไม่ทำในงานนี้)

- SP adapter (motordb/centerdb) เฟสถัดไป
