# Implementation Tasks: Purchase Flow Completion (ปิด flow ซื้อประกันภัย End-to-End)

> Status: approved 2026-08-02

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Fix cart add-item 409 — `ValueGeneratedNever()` บน Cart Item.Id ทั้ง 2 mirror configuration + empty migration sync snapshot + regression test 2 DI scope (`CreateCartHandler` → `AddItemToCartHandler`) + แก้ `InsuranceCheckoutEndToEndTests` ให้เดิน handler จริง (ลบ `new Cart(...)` workaround + comment ที่โทษ rls-to-query-filter ผิด) — done = add-item ผ่านทั้ง SQLite E2E และ endpoint จริง
     Satisfies: REQ-1 (all criteria). Verify: `dotnet test tests/Carts.Tests tests/Hosts.Tests --filter InsuranceCheckout` + fresh-DB `ef database update` ไม่มี PendingModelChangesWarning.
     Evidence:
       - test: `dotnet test tests/Carts.Tests` -> 15 passed / 0 failed (REQ-1.2/1.3 พฤติกรรมเดิมคงไว้)
       - test: `dotnet test tests/Hosts.Tests --filter InsuranceCheckout` -> 6 passed / 0 failed รวม regression 2-scope ใหม่ `Adding_an_item_to_a_cart_created_by_an_earlier_request_inserts_the_line` (REQ-1.1) — `CreateCartHandler` ใน context แรก, `AddItemToCartHandler` ใน context ที่สอง, merge ซ้ำใน context ที่สาม
       - test: `dotnet test tests/Hosts.Tests` -> 386 passed / 0 failed (suite เต็ม รวม `ModelConsistencyTests` = ไม่มี pending model change)
       - test: `dotnet test tests/Architecture.Tests --filter CartItemAggregateBoundary|MoneyColumnMapping` -> 6 passed / 0 failed
       - regression proof (REQ-1.4): revert `ValueGeneratedNever()` เป็น `ValueGeneratedOnAdd()` ชั่วคราวบน mirror `Persistence.MerchantRuntime` -> `--filter InsuranceCheckout` = 6 failed / 0 passed ทุกข้อด้วย `ConcurrencyConflictException` ที่ `AddItemToCartHandler.cs:32` (inner: expected 1 row affected, actually 0) แล้ว restore กลับ -> 6 passed
       - migration: `20260802134226_CartItemClientMintedId` มี `Up`/`Down` ว่างจริง (snapshot-sync ล้วน — snapshot diff เหลือแค่ลบ `.ValueGeneratedOnAdd()` ออกจาก `Carts.Domain.Items.Item.Id`); fresh-DB replay จากศูนย์บน catalog เปล่า `VCentralPayT1Probe` (:11433) -> ทุก migration applied รวม `20260802134226_CartItemClientMintedId` ปิดท้าย `Done.` ไม่มี PendingModelChangesWarning; dev DB `VCentralPay` -> `No migrations were applied. The database is already up to date.` ไม่มี warning
       - viewports: n/a — logic-only
       - deviations: (1) verify command ตามตัวอักษรรันไม่ได้ — `dotnet test` รับได้ project เดียว (MSB1008) จึงแยกเป็น 2 คำสั่ง; (2) regression 2-scope วางไว้ใน `InsuranceCheckoutEndToEndTests` (ตาม verify command ของ task นี้) ไม่ใช่ `MerchantLifecycleEndpointTests` ที่ design.md ระบุ — ไฟล์นั้นเป็นของ T2 ซึ่งจะเพิ่ม regression ระดับ endpoint ทับอีกชั้น; (3) probe DB `VCentralPayT1Probe` (ของ T1) และ `VCentralPayFreshT1` (ของ teammate ก่อนหน้า) ยังค้างบน :11433 — `destructive-guard` block `DROP DATABASE` โดยต้องให้ user ยืนยัน จึงไม่ลบเอง (ไม่ bypass hook), dev DB `VCentralPay` ไม่ถูกแตะ
- [ ] 2. ปิดวงจร cart–checkout — freeze cart ตอน StartCheckout (endpoint orchestration + `MarkCartCheckedOutCommand`), `GetOpenForCartAsync` pre-check + filtered unique index `IX_CheckoutSessions_CartId_Open` (2 mirror, named overload), endpoint `POST /checkouts/{id}/abandon` (gate merchant-user+CSRF) + แก้ `Session.Abandon()` Abandoned→no-op + `Cart.Reopen()`/`ReopenCartCommand`, map cart mutation exception → 409 — done = start ซ้ำ 409 ทุกทาง, abandon→restart ได้
     Satisfies: REQ-2 (all criteria). Depends on: 1. Verify: `dotnet test tests/Carts.Tests tests/Checkouts.Tests tests/Hosts.Tests --filter MerchantLifecycle` + Integration.Tests เคส index บน :11433.
- [ ] 3. `PaymentConfirmationService` + session expiry — แตกเส้น fetch→verify→claim→mark→enqueue ออกจาก `HandlePspWebhookHandler` เป็น service เดียว (key แชร์ `charge:{id}:confirmed`, enqueue-on-transition, outcome `Conflicted` สำหรับ terminal+Paid → webhook ตอบ 200 + LogCritical, branch PSP `Failed` → `MarkFailed`), เพิ่ม `Session.OpenTtl`/`IsExpiredAt` + lazy expire verify-first ใน `CreateSessionHandler` (2-phase SaveChanges ใน tx เดียว) — done = webhook behavior เดิมไม่เปลี่ยน (test เดิมเขียวหมด) + expiry ครบทุก branch
     Satisfies: REQ-3 (all criteria). Verify: `dotnet test tests/Payments.Tests` + Integration.Tests เคส 2-phase expire+insert ผ่าน `IX_PaymentSessions_OrderId_Open`.
- [ ] 4. Order cancel — `ReleaseOpenSessionCommand` (ผ่าน `PaymentConfirmationService`: ไม่มี open → ok / ไม่มี chargeId → expire / มี chargeId → fetch ก่อน / สด → 409) + `CancelOrderCommand` + endpoint `POST /orders/{orderId}/cancel` (merchant-user + CSRF, ไม่มี iam key) — done = cancel ได้เมื่อปลอดภัยเท่านั้น idempotent
     Satisfies: REQ-4 (all criteria). Depends on: 3. Verify: `dotnet test tests/Orders.Tests tests/Payments.Tests tests/Hosts.Tests --filter Cancel`.
- [ ] 5. กรองเอกสารขายแล้ว + double-sell signal — post-filter แถว PAID ใน `ListProductsHandler` เมื่อ filter ที่ส่งให้ SP คือ UNPAID, `OrderPaid` + `OrderId` (additive), `Product.MarkPaid(paidDate, orderId)` + คอลัมน์ `SoldOrderId`, `LogCritical` เฉพาะ `SoldOrderId` มีค่าและต่าง order (replay เงียบ) — done = เอกสารขายแล้วหายจากหน้า UNPAID
     Satisfies: REQ-5 (all criteria). Verify: `dotnet test tests/Products.Tests tests/Orders.Tests` + Integration.Tests เคสกรอง (fixture ตัวเอง ห้ามแตะ seed 42 แถว SaleCode 77001).
- [ ] 6. Checkout/Order enrichment — `PaymentChannel`/customer 3 field/`Discount: Money` บน `Checkouts.Session`+Items → `CheckoutConfirmed` (additive) → `Orders.Order`+Items, `NotificationRecipient = CustomerPhone ?? CustomerEmail ?? Recipient`, `IOrderNoSequence` port + `shop.OrderNoSeq` + GRANT + `BypassPrimitiveTests.AllowedPorts`, migration เขียนมือลำดับ nullable→backfill(OrderNo + แตก Recipient)→NOT NULL→unique index→DROP Recipient (ทุกคอลัมน์ NOT NULL มี DB DEFAULT), read surfaces (`GET /orders` + SFS `filters=orderNo:eq:`, detail, summary +orderNo), `CustomerOrderNotification`+OrderNo — done = order ใหม่มี OrderNo/channel/discount/customer ครบ + payload เก่า replay ได้
     Satisfies: REQ-6 (6.2-6.8), REQ-7 (all criteria). Verify: `dotnet test tests/Checkouts.Tests tests/Orders.Tests` + Integration.Tests sequence/GRANT + fresh-DB migration proof บน DB มีข้อมูล.
- [ ] 7. 2C2P 3 ช่องทาง — ขยาย `TwoCTwoPAdapter.SupportedMethods` + `PaymentChannelFor` (`card`→CC, `promptpay`→QR, `installment`→IPP), อัปเดต `EnabledMethods` ใน seed-demo + บันทึก ops step สำหรับ connection เดิม, eligibility check ตอน `POST /checkouts` (channel ที่ connection ไม่รองรับ → 400) — done = เลือกได้เฉพาะช่องทางที่ชาร์จได้จริง
     Satisfies: REQ-6 (6.1). Depends on: 6. Verify: `dotnet test tests/Payments.Tests --filter TwoCTwoP` + `tests/Hosts.Tests --filter Checkout`.
- [ ] 8. Customer payment path — `POST /orders/{token}/pay` + `POST /orders/{token}/payment-status` (AllowAnonymous + policy `customer-payment` ~10/นาที/IP + short-circuit terminal), `ConfirmPaymentStatusCommand` (4 ค่า รวม `Cancelled`), `OrderSummary` contract change (+MerchantId ห้าม leak, +OrderNo, +PaymentChannel, −PaymentSessionId), token หมดอายุ → 404, `GET /payments/sessions/{id}` + แก้ `GetSessionHandler` → `NotFoundException` — done = ลูกค้าจ่ายครบวงจาก summary link ถึงหน้าสถานะ
     Satisfies: REQ-8 (all criteria). Depends on: 3, 6, 7. Verify: `dotnet test tests/Payments.Tests tests/Hosts.Tests --filter "MerchantLifecycle|CustomerPayment"` + manual E2E บน dev (sandbox 2C2P).

## Suggested execution batches

> Feature นี้ COUPLED หนัก (contracts + migration + `PaymentConfirmationService` แชร์ข้ามงาน) —
> DEFAULT = รันทุก task ใน session เดียว: `scripts/pane-loop.sh purchase-flow-completion all-in-one`
> (หรือ `/spec-implement all`) ตามลำดับ 1→2→3→4→5→6→7→8
> ไม่มี Batch tag — ไม่มีคู่ task ที่เล็ก+ชนิดเดียวกันพอจะได้ประโยชน์; task 3 กับ 6 เป็น
> foundational/core ควรได้ context สด ถ้าจะแยก pane ให้แยกที่รอยต่อ PR: (1,2) / (3,4,5) / (6,7,8)
