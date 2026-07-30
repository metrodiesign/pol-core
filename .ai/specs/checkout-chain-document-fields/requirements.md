# Requirements: checkout-chain-document-fields

> Status: approved-for-implementation 2026-07-30 (อนุมัติผ่าน plan โดย user — โหมด quick, ไม่มี gate ต่อ artifact)
> Scope: rework snapshot chain Carts -> Checkouts -> Orders ให้ snapshot field เอกสารประกันจริง แล้วถอด bridge บน `ProductView` ทิ้ง (ต่อยอด PR #143 ที่ pivot Product เป็นเอกสารประกันตาม VCentralPay SP guide)

## บริบท

PR #143 เปลี่ยน `Product` เป็นเอกสารประกัน (DocumentNo/ProductGroup/DocumentType/TotalPremium/StartDate/EndDate/...) แต่ Checkouts/Orders ยัง snapshot field แผนประกันเก่า 3 ตัว (`SumInsured`, `CoverageDurationDays`, `Insurer`) ผ่าน bridge properties บน `ProductView` ซึ่งความหมายผิด (`SumInsured => TotalPremium`) — ยืนยันแล้วว่า 3 field นี้เป็น snapshot/display ล้วน ไม่มี logic คำนวณจากมัน และ `Contracts/CheckoutConfirmed` ไม่มี live-traffic compat ต้องปกป้อง (pre-prod)

## Requirements (EARS)

### REQ-1 — snapshot ของ line ต้องเป็น field เอกสารจริง

- 1.1 WHEN merchant เริ่ม checkout (POST /checkouts), THE SYSTEM SHALL snapshot จาก `ProductView` ลง checkout line ด้วยชุด field: `DocumentNo` (required), `ProductGroup` (required, wire value string เช่น "VMI"), `DocumentType` (required, wire value string), `PolicyNumber` (nullable), `StartDate`/`EndDate` (nullable datetime2(0))
- 1.2 THE SYSTEM SHALL NOT เก็บ `SumInsured`, `CoverageDurationDays`, `Insurer` บน checkout/order line อีกต่อไป (ทั้ง CLR และ column)
- 1.3 THE SYSTEM SHALL คง `UnitPrice` (Money) และ insured PII fields (`InsuredFirstName/LastName/IdNumber/DateOfBirth`) ไว้ตามเดิมทุกประการ
- 1.4 WHEN สร้าง line (`Checkouts.Item` / `Orders.Item`), THE SYSTEM SHALL reject ค่าว่าง/whitespace ของ `DocumentNo`/`ProductGroup`/`DocumentType` (trim ก่อนเก็บ) และ reject `StartDate > EndDate` เมื่อมีทั้งคู่ ด้วย `ArgumentException`

### REQ-2 — integration event

- 2.1 THE SYSTEM SHALL เปลี่ยน `Contracts.CheckoutConfirmedItem` เป็น shape ใหม่ตาม REQ-1.1 (แทนที่ 3 field เก่า) โดยไม่เพิ่ม version ใหม่ (pre-prod, ไม่มี in-flight compat)
- 2.2 WHEN `CheckoutConfirmed` ถูก consume, THE SYSTEM SHALL map field ใหม่เข้า `OrderItemInput` แบบ 1:1 ไม่มี transform

### REQ-3 — ถอด bridge

- 3.1 THE SYSTEM SHALL ลบ bridge properties `Price`, `SumInsured`, `CoverageDurationDays`, `Insurer` ออกจาก `ProductView` (รวม `ponytail:` comments)
- 3.2 WHEN cart endpoint ตั้งราคา line, THE SYSTEM SHALL ใช้ `product.TotalPremium` ตรง ๆ (แทน `product.Price` เดิม)
- 3.3 หลังงานเสร็จ `grep -rn "ponytail: bridge" src/` SHALL ว่าง

### REQ-4 — read models

- 4.1 THE SYSTEM SHALL แทน 3 field เก่าใน `OrderItemDetail` (GetOrderDetail) และ `OrderItemListItem` (GetOrders) ด้วยชุด field ใหม่ตาม REQ-1.1
- 4.2 THE SYSTEM SHALL คงพฤติกรรม reveal-audit (detail = เลขเต็ม + audit) และ masking (list = `MaskedInsuredIdNumber`) เดิมทุกประการ

### REQ-5 — DB

- 5.1 THE SYSTEM SHALL มี migration เดียวที่ alter `shop.CheckoutSessionItems` และ `shop.OrderItems`: drop `SumInsuredAmount`, `SumInsuredCurrency`, `CoverageDurationDays`, `InsurerName` / add `DocumentNo` nvarchar(150) NOT NULL, `ProductGroup` varchar(10) NOT NULL, `DocumentType` varchar(20) NOT NULL, `PolicyNumber` varchar(150) NULL, `StartDate` datetime2(0) NULL, `EndDate` datetime2(0) NULL (alter — ไม่ drop ตาราง จึงไม่ต้อง re-GRANT)
- 5.2 THE SYSTEM SHALL อัปเดต `docker/bootstrap/seed-demo.sql` (INSERT `shop.OrderItems`) ให้ตรง column ชุดใหม่ และ seed-demo ทั้งไฟล์ต้องรันผ่านบน dev DB
- 5.3 EF configs ของ line ทั้ง 4 ไฟล์ (dual-config ต่อโมดูล) SHALL mapping ตรงกันเป๊ะ

### REQ-6 — คุณภาพรวม

- 6.1 `dotnet build pol-core.slnx -warnaserror` SHALL ผ่าน 0 error/0 warning
- 6.2 test suite ทั้งหมด (รวม Integration.Tests บน :11433) SHALL เขียว
- 6.3 `grep -rn "SumInsured\|CoverageDurationDays\|InsurerName" src/Modules/Checkouts src/Modules/Orders src/Contracts` SHALL ว่าง — ยกเว้น `Orders.Domain/Items/ItemPolicy*` (external-reference data คนละแกน ไม่อยู่ในขอบเขต)

## นอกขอบเขต (จงใจ)

- policy report / SFS / OrderSummaryReader (ไม่แตะ 3 field — ยืนยันจากการสำรวจ)
- `Product.MarkPaid` wiring ตอน order paid (follow-up แยก — บันทึกใน HANDOFF)
- event versioning / outbox drain (pre-prod)
- insuredPersons flow (PII ผู้ซื้อกรอก — คงเดิม)
