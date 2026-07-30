# Implementation Tasks: checkout-chain-document-fields

> Status: approved-for-implementation 2026-07-30
> Each task is a cohesive, independently verifiable slice. อ่าน `requirements.md` + `design.md` + `HANDOFF.md` ก่อนเริ่มทุกครั้ง
> Branch: `feat/products-insurance-document` (PR #143). ห้าม push develop, ห้าม force push
> Traps ทั้งหมดอยู่ใน design.md หัวข้อ "Traps" — อ่านก่อนเริ่มทุก task

- [x] 1. **Checkouts + Contracts** — เปลี่ยน `CheckoutConfirmedItem` (`src/Contracts/CheckoutConfirmed.cs`), `CheckoutItemInput`, `Checkouts.Domain/Items/Item.cs` (property/ctor/invariant ใหม่ตาม design ลำดับ param กลาง), `Session.cs` ส่งผ่าน, `ConfirmCheckout.cs` mapper, dual config Checkouts 2 ไฟล์, แก้ `tests/Checkouts.Tests/ConfirmCheckoutTests.cs`
     **Done** = `dotnet build src/Modules/Checkouts/Checkouts.Infrastructure src/Contracts src/Persistence/Persistence.MerchantRuntime` อาจยังแดงที่โปรเจกต์อื่น (Orders/Hosts รอ task 2-3) แต่ Checkouts.Domain/Application/Infrastructure + Contracts ต้อง compile; `dotnet test tests/Checkouts.Tests` เขียว
     Satisfies: REQ-1 (1.1-1.4), REQ-2 (2.1), REQ-5 (5.3 ฝั่ง Checkouts)
     Evidence: `dotnet build src/Contracts` -> `ok dotnet build: 2 projects, 0 errors, 0 warnings`; `dotnet build src/Modules/Checkouts/Checkouts.Infrastructure` -> `ok dotnet build: 7 projects, 0 errors, 0 warnings`; `dotnet build src/Persistence/Persistence.MerchantRuntime` -> error เฉพาะ `Orders.Application/CheckoutConfirmedConsumer.cs(40,*)` (SumInsured/CoverageDurationDays/Insurer) = นอกขอบเขต task 1 รอ task 2 — ไม่มี error ในไฟล์ Persistence/Checkouts เอง (`dotnet build ... | grep error | grep -v Orders` ว่าง); `dotnet test tests/Checkouts.Tests` -> `Passed! - Failed: 0, Passed: 13, Skipped: 0, Total: 13`. Viewports 375/768/1440: n/a (ไม่มี UI). Deviations: ไม่มี

- [ ] 2. **Orders** — `Orders.Domain/Items/{Item,OrderItemInput}.cs`, `Order.cs` ส่งผ่าน, `CheckoutConfirmedConsumer.cs` map 1:1, read models `GetOrderDetail.cs`/`GetOrders.cs` field ชุดใหม่ (คง reveal-audit/masking เดิม), dual config Orders 2 ไฟล์, แก้ tests `Orders.Tests/{OrderItemsTests,GetOrderDetailTests,GetOrdersTests,CheckoutConfirmedConsumerTests,Fakes}.cs`
     **Done** = `dotnet test tests/Orders.Tests` เขียว; Orders.* ทุกโปรเจกต์ compile
     Satisfies: REQ-1 (1.2-1.4), REQ-2 (2.2), REQ-4 (4.1, 4.2), REQ-5 (5.3 ฝั่ง Orders)
     Evidence:

- [ ] 3. **Host wiring + ถอด bridge** — `Program.cs` POST /checkouts snapshot ชุดใหม่ + cart endpoint ใช้ `product.TotalPremium`; ลบ bridge 4 ตัว (`Price`/`SumInsured`/`CoverageDurationDays`/`Insurer`) จาก `Products.Application/ProductView.cs`; แก้ tests `Hosts.Tests/{InsuranceCheckoutEndToEndTests,WorkerWriteFloorTests}.cs`, `Architecture.Tests/{OrderItemsTests,PaymentPricingQueryTests}.cs`
     **Done** = `dotnet build` ทั้ง solution 0 error; `dotnet test tests/Hosts.Tests tests/Architecture.Tests tests/Products.Tests` เขียว; `grep -rn "ponytail: bridge" src/` ว่าง; grep REQ-6.3 ว่าง
     Satisfies: REQ-1 (1.1), REQ-3 (3.1-3.3), REQ-6 (6.3)
     Evidence:

- [ ] 4. **Migration + seed + integration** — `dotnet ef migrations add CheckoutChainDocumentFields` (alter `shop.CheckoutSessionItems` + `shop.OrderItems` ตาม REQ-5.1, ไม่ re-GRANT); อัปเดต `docker/bootstrap/seed-demo.sql` INSERT shop.OrderItems; แก้ raw-SQL ใน `Integration.Tests/{OrderSummaryReaderIntegrationTests,OrderItemPolicyGrantsTests}.cs`; apply จริงบน :11433 + รัน seed-demo ทั้งไฟล์ + Integration.Tests
     **Done** = `dotnet ef database update` ผ่าน; seed-demo รันผ่าน (`shop.OrderItems = 4`); `source .env.integration` + `dotnet test tests/Integration.Tests` เขียว
     Satisfies: REQ-5 (5.1, 5.2)
     Evidence:

- [ ] 5. **Full gate + PR** — `dotnet build pol-core.slnx -warnaserror`; `dotnet test` ทุก non-integration suite + Integration; `bash scripts/check-rename-identifiers.sh`; `bash scripts/spec-trace.sh checkout-chain-document-fields`; ยืนยัน working tree สะอาด + `git show --stat` ครบ; push branch; อัปเดต PR #143 body (เพิ่มหัวข้อ chain rework + ถอด bridge, ลบหมายเหตุ bridge เดิม)
     **Done** = ทุก gate เขียว + PR #143 อัปเดตแล้ว
     Satisfies: REQ-6 (6.1, 6.2)
     Evidence:
