# Tasks: bugfix-order-paid-link

> Status: approved 2026-07-04

- [x] T1 — repro + regression tests (RED ก่อนแก้)
  Evidence:
    - test: `dotnet test tests/Orders.Tests` (ก่อนแก้ T2) -> Failed: 3, Passed: 22 — repro RED ตามเป้า:
      F1 `It_marks_the_order_paid_by_the_events_OrderId` fail "Expected: Paid / Actual: AwaitingPayment";
      F3 `It_lets_an_amount_mismatch_escape_without_transitioning` + F4
      `It_lets_a_cancelled_order_payment_escape_without_transitioning` fail "No exception was thrown";
      B1/B2 เขียว (pin พฤติกรรมเดิม)
    - ไฟล์ใหม่: `tests/Orders.Tests/OrderPaidConsumerTests.cs` (5 tests)
    - B3: มี assertion อยู่แล้ว — `CheckoutConfirmedConsumerTests.It_skips_when_an_order_already_exists_for_the_session`
      (+ notification ใน `It_creates_an_order_and_enqueues_the_notification_for_a_new_checkout`) — อ้างของเดิม ไม่ต้องเติม
    - B5: มี assertion อยู่แล้ว — `tests/BuildingBlocks.Tests/OutboxSerializerTests.cs` 3 tests
      (record-equality round-trip ครอบ OrderId, Money amount+currency explicit, camelCase) — อ้างของเดิม ไม่ต้องเติม
    - viewports: n/a — logic-only
    - deviations: B3/B5 ครบอยู่แล้วจึงไม่เติม assertion ใหม่ (ตามเงื่อนไข "มีแล้วให้อ้างชื่อ test")
  - ไฟล์ใหม่ `tests/Orders.Tests/OrderPaidConsumerTests.cs` — ทดสอบ `OrderPaidConsumer.Handle`
    ผ่าน fake `IOrderRepository` + fake `IUnitOfWork` เดิมใน `tests/Orders.Tests/Fakes.cs`
    (fake repo มี `GetAsync` อยู่แล้ว)
  - repro F1+F2 (ต้อง RED ตอนนี้): สร้าง order แบบ production path —
    `Order.Create(tenantId, amount, createdAt, checkoutSessionId: X)` (ไม่ส่ง `paymentSessionId`
    เหมือน `CheckoutConfirmedConsumer.cs:38-40`) ใส่ repo → `Handle(new PaymentPaid(
    PaymentSessionId: Guid ใหม่, OrderId: order.Id, tenantId, Amount: ยอดตรง, ...))` →
    assert observable: `order.Status == OrderStatus.Paid`, `order.PaidAt == OccurredAt`,
    domain event `OrderPaid` ถูก raise 1 ครั้ง, `SaveChangesAsync` ถูกเรียก
  - F3 (RED ตอนนี้ — ปัจจุบัน return เงียบ): order `AwaitingPayment` + `PaymentPaid` ยอด/สกุลไม่ตรง →
    assert throw `InvalidOperationException` + `order.Status` คง `AwaitingPayment` + ไม่มี event
    (throw ทะลุ = dispatcher `MarkFailed` → retry → DLQ ตาม policy)
  - F4 (RED ตอนนี้): order ที่ `Cancel()` แล้ว + `PaymentPaid` → assert throw + `Status` คง
    `Cancelled` + ไม่มี event
  - B1: order ที่ `Paid` แล้ว (MarkPaid ก่อน 1 ครั้ง) + `PaymentPaid` ซ้ำ → ไม่ throw,
    `Status` คง `Paid`, ไม่มี `OrderPaid` เพิ่ม (events รวม 1), `SaveChangesAsync` ไม่ถูกเรียกซ้ำ
  - B2: `PaymentPaid` ที่ `OrderId` ไม่มีใน repo → ไม่ throw, return ปกติ (ack) — pin null-branch เดิม
  - B3: ตรวจ `tests/Orders.Tests/CheckoutConfirmedConsumerTests.cs` — ถ้ายังไม่มี assertion
    idempotency (consume `CheckoutConfirmed` ซ้ำ → ไม่สร้าง order ที่สอง) ให้เพิ่ม 1 test; มีแล้วให้อ้างชื่อ test ใน Evidence
  - B5: ตรวจ `tests/Orders.Tests/OutboxSerializerTests.cs` — ต้องมี assertion round-trip
    `PaymentPaid` ครอบ `OrderId` + `Money` (amount+currency); ขาดให้เติม assertion
  - รัน `dotnet test tests/Orders.Tests` → test ใหม่ F1-F4 ต้องแดง (repro ยืนยัน defect), ที่เหลือเขียว
  - Satisfies: F1, F2, F3, F4, B1, B2, B3, B5

- [x] T2 — แก้ consumer ให้ resolve ด้วย OrderId + เก็บกวาด orphan ของการแก้
  Evidence:
    - test: `dotnet test tests/Orders.Tests` -> Passed: 25 / Failed: 0 — F1-F4 จาก T1 พลิก RED->GREEN ครบ
    - แก้: `OrderPaidConsumer.cs` lookup -> `GetAsync(notification.OrderId)` + doc comment ตรงความจริง;
      คง null-branch ack (B2); exception จาก `MarkPaid` ทะลุถึง dispatcher (F3/F4 -> DLQ)
    - ลบ orphan: `GetByPaymentSessionIdAsync` ออกจาก `IOrderRepository.cs` + `OrderRepository.cs` +
      `Fakes.cs` — grep ยืนยันเหลือ 0 อ้างอิงทั้ง src/ + tests/
    - `Order.cs` doc comment ของ `AttachPaymentSession` แก้เป็น legacy link (method/property/column คงไว้ ไม่มี schema change)
    - ไม่แตะไฟล์ใต้ `src/Modules/Payments/` (B4)
    - viewports: n/a — logic-only
    - deviations: none
  - `src/Modules/Orders/Orders.Application/OrderPaidConsumer.cs:31-33`: เปลี่ยน lookup จาก
    `GetByPaymentSessionIdAsync(notification.PaymentSessionId)` → `GetAsync(notification.OrderId)`;
    คง null-branch ack-and-return (B2) และคงการปล่อย exception จาก `MarkPaid` ทะลุ (F3/F4);
    อัปเดต doc comment ของ class (บรรทัด 11 "Loads the order by PaymentSessionId") ให้ตรงความจริง
  - grep ยืนยัน `GetByPaymentSessionIdAsync` เหลือ 0 callers แล้วลบออกจาก
    `src/Modules/Orders/Orders.Application/IOrderRepository.cs:13-14`,
    `src/Modules/Orders/Orders.Infrastructure/OrderRepository.cs:20-22`,
    fake ใน `tests/Orders.Tests/Fakes.cs` (orphan ที่เกิดจากการแก้นี้)
  - `src/Modules/Orders/Orders.Domain/Order.cs:90-93`: แก้ doc comment ของ `AttachPaymentSession`
    ที่อ้างว่า session คือ join key ของ consumer (เท็จหลังแก้) — ระบุเป็น legacy link;
    **ห้ามลบ** method/property/column `PaymentSessionId` (pre-existing, ไม่ใช่ orphan ของการแก้นี้;
    ไม่มี schema change ใน fix นี้)
  - ห้ามแตะฝั่ง Payments / webhook / outbox dispatcher (B4)
  - รัน `dotnet test tests/Orders.Tests` → F1-F4 จาก T1 ต้องเขียว (RED→GREEN ครบ)
  - Satisfies: F1, F2, F3, F4, B2
- [x] T3 — verify ทั้งระบบ
  Evidence:
    - test: `dotnet build -warnaserror` -> 0 Warning / 0 Error
    - test: `dotnet test` (ทั้ง solution, env :11434 sourced) -> ทุก suite เขียว: SharedKernel 39,
      Cart 15, Producer 95, Orders 25, Checkout 2, Tenant 31, BuildingBlocks 65, Products 25,
      Payments 55 (B4), Architecture 48, Admin 80, Hosts 195, Integration 62 (B6)
    - Integration รอบแรกแดง 1: `OrdersReconciliationIntegrationTests.The_aggregate_excludes_another_tenants_orders`
      "Expected: 555 / Actual: 3885" = flake สะสมที่รู้จัก (marker QQQ ค้าง 7 รอบบน DB reuse —
      ลบ residue 7 แถวใต้ TenantB binding แล้วรันใหม่ -> `dotnet test tests/Integration.Tests`
      Passed: 62 / Failed: 0) — ไม่เกี่ยว diff (ไม่แตะ reconciliation path)
    - `git status`: diff ไม่มีไฟล์ใต้ `src/Modules/Payments/` และไม่มี migration ใหม่ (B4 + no schema change)
    - `scripts/spec-trace.sh bugfix-order-paid-link` -> bugfix spec ไม่มี requirements.md — ข้าม traceability ตามพฤติกรรม script
    - viewports: n/a — logic-only
    - deviations: none
  - `dotnet build -warnaserror` → 0 error
  - `dotnet test` ทั้ง solution เขียว — pin B4 (Payments.Tests เดิมเขียวโดยไม่แตะไฟล์ฝั่ง Payments:
    ยืนยันด้วย `git status` ว่า diff ไม่มีไฟล์ใต้ `src/Modules/Payments/` และไม่มี migration ใหม่)
  - integration tests (B6): `source .env.integration` + container :11434 →
    `dotnet test tests/Integration.Tests` — `OrdersReconciliationIntegrationTests` เขียว
    (หมายเหตุ: OrdersReconciliation flaky ตอน re-run บน DB ค้าง — เขียวบน fresh/CI ตาม memory)
  - Satisfies: B4, B6
