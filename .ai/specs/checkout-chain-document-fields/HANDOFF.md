# HANDOFF: checkout-chain-document-fields

Rolling handoff — teammate ทุกคน **append หัวข้อใหม่ท้ายไฟล์** หลังจบ task ของตัวเอง (สิ่งที่ทำ / ไฟล์ที่แตะ / กับดักที่เจอจริง / สถานะ test / สิ่งที่คนถัดไปต้องรู้). ห้ามแก้หัวข้อของคนก่อน

## จาก lead (Fable) — บริบทตั้งต้น 2026-07-30

- Branch `feat/products-insurance-document`, PR #143 OPEN (Products pivot เป็นเอกสารประกัน merge แล้วใน branch นี้, commit `9e87c51`)
- Bridge ที่ต้องถอดอยู่ `src/Modules/Products/Products.Application/ProductView.cs` (~:47-60): `Price`/`SumInsured`/`CoverageDurationDays`/`Insurer` — จุดใช้มี 2 ที่ใน `src/Hosts/Api/Program.cs`: cart (`~:663-668` ใช้ `IsActive`+`Price`) และ checkout (`~:770-780` snapshot 3 field เก่า)
- ตำแหน่งไฟล์ทั้งหมด + ลำดับ param กลาง + traps: อ่าน `design.md` (สำรวจยืนยันแล้ว 2026-07-30 — เชื่อได้ ไม่ต้อง re-explore กว้าง)
- 3 field เก่าไม่มี logic คำนวณอิงอยู่ — ลบได้ตรง ๆ; policy report / OrderSummaryReader ไม่แตะ — ห้ามไปยุ่ง
- dev DB :11433 สถานะ: migration `20260730072057_ProductsInsuranceDocument` apply แล้ว, shop.Products = 100 (seed-demo) + 6 (e6 migration seed); หมายเหตุ: seed-demo assert `merch.Merchants = 0` fail เป็นสภาพ local เดิม (merchant `vprivilege` คนละ Id ค้าง) ไม่เกี่ยวงานเรา
- Follow-up ที่จดไว้แล้ว ไม่ทำรอบนี้: wire `Product.MarkPaid`/`Deactivate` ตอน order paid; SP adapter

## chain-t1 — Task 1: Checkouts + Contracts (2026-07-30)

**สิ่งที่ทำ**: เปลี่ยน snapshot field เก่า 3 ตัว (`SumInsured`/`CoverageDurationDays`/`Insurer`) เป็นชุดเอกสาร 6 ตัวตาม design ตลอดเส้น Checkouts + Contracts

**ไฟล์ที่แตะ** (8):
- `src/Contracts/CheckoutConfirmed.cs` — `CheckoutConfirmedItem` positional ใหม่ + doc comment
- `src/Modules/Checkouts/Checkouts.Domain/Items/CheckoutItemInput.cs`
- `src/Modules/Checkouts/Checkouts.Domain/Items/Item.cs` — property + ctor + invariant ใหม่
- `src/Modules/Checkouts/Checkouts.Domain/Session.cs` — ส่งผ่าน
- `src/Modules/Checkouts/Checkouts.Application/ConfirmCheckout.cs` — mapper -> event
- `src/Modules/Checkouts/Checkouts.Infrastructure/Items/ItemConfiguration.cs`
- `src/Persistence/Persistence.MerchantRuntime/Checkouts/Items/ItemConfiguration.cs` (identical block กับข้างบน)
- `tests/Checkouts.Tests/ConfirmCheckoutTests.cs`

**สถานะ build/test**: `src/Contracts` 0/0; `Checkouts.Infrastructure` (7 โปรเจกต์) 0/0; `Persistence.MerchantRuntime` แดงเฉพาะที่ `Orders.Application/CheckoutConfirmedConsumer.cs:40` (3 error = field เก่า) ซึ่งเป็นงาน task 2; `dotnet test tests/Checkouts.Tests` 13/13 เขียว

**กับดักที่เจอจริง**:
1. `dotnet build a b c` หลายโปรเจกต์ใน call เดียว = MSB1008 (`Only one project can be specified`) — ต้องวน build ทีละโปรเจกต์
2. task gate ยิงตอน Edit ที่ flip `[x]` แม้ยังไม่ได้เติม Evidence — **flip ถูกเขียนลงไฟล์แล้วถึงจะ block** ไม่ใช่ rollback; แก้โดย Edit ต่อเติมบรรทัด Evidence ทันที (สรุป: อย่าแยก flip กับ Evidence)
3. Evidence line ต้องมี `Viewports 375/768/1440: n/a` + `Deviations:` ด้วย ไม่ใช่แค่ผล test

**สิ่งที่ chain-t2 (Orders) ต้องรู้**:
- ลำดับ param กลางที่ใช้จริง: `ProductId, Quantity, UnitPrice, DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate, InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth` — `CheckoutConfirmedItem` เป็น shape นี้แล้ว map เข้า `OrderItemInput` ได้ 1:1
- CLR types: `string DocumentNo`, `string ProductGroup`, `string DocumentType`, `string? PolicyNumber`, `DateTime? StartDate`, `DateTime? EndDate`
- invariant ที่ `Checkouts.Item` ใช้ (ทำแบบเดียวกันใน `Orders.Item` เพื่อ defense in depth): `ArgumentException.ThrowIfNullOrWhiteSpace` 3 ตัว + Trim, `if (startDate is not null && endDate is not null && startDate > endDate) throw new ArgumentException(..., nameof(startDate))`; `PolicyNumber` ไม่ trim (nullable ปล่อยตามที่มา)
- EF block ที่ใช้จริง (copy ไปใช้กับ Orders ทั้ง 2 ไฟล์ให้เหมือนกัน):
  `DocumentNo` `.HasMaxLength(150).IsRequired()` / `ProductGroup` `.HasMaxLength(10).IsUnicode(false).IsRequired()` / `DocumentType` `.HasMaxLength(20).IsUnicode(false).IsRequired()` / `PolicyNumber` `.HasMaxLength(150).IsUnicode(false)` / `StartDate`+`EndDate` `.HasPrecision(0)` — ลบทั้ง `ComplexProperty(x => x.SumInsured, ...)`, `CoverageDurationDays`, `Insurer` (column `InsurerName`)
- error ที่ chain-t2 จะเห็นตอนเริ่ม: `CheckoutConfirmedConsumer.cs(40,57/71/95)` CS1061 x3 — คาดไว้แล้ว
- test helper pattern ที่ใช้ใน Checkouts.Tests: static `Line(...)` มี default param ทุกตัว แล้ว test แต่ละอันส่งเฉพาะ field ที่สนใจ — ลดการแก้ literal ซ้ำ ๆ ตอน signature เปลี่ยน (แนะนำทำแบบเดียวกันใน Orders.Tests)
