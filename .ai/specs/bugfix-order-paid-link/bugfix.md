# Bugfix: Order ค้าง AwaitingPayment เพราะ OrderPaidConsumer หา order ไม่เจอ

> Status: approved 2026-07-04

## Current Behavior (Defect)

WHEN ลูกค้าจ่ายเงินสำเร็จ (PSP webhook ผ่าน verify → `PaymentSession` เป็น `Paid` → emit `PaymentPaid`
ผ่าน outbox → Worker dispatch) THEN Order ที่คู่กัน**ค้างสถานะ `AwaitingPayment` ตลอดไป** —
`OrderPaidConsumer` หา order ไม่เจอและ return เงียบ, outbox mark message เป็น `Processed`
(ไม่ retry, ไม่มี log, ไม่มีร่องรอย)

สายเหตุการณ์ (file:line, ยืนยันโดย bug-investigator 2026-07-04):

1. `HandlePspWebhookHandler.cs:92-100` emit `PaymentPaid(session.Id, session.OrderId, ...)` — `OrderId` มีค่า valid เสมอ (`PaymentSession.cs:87-88` validate ตอน create)
2. `OutboxDispatcher.cs:106-133` dispatch → `OrderPaidConsumer.cs:31-33` lookup ด้วย `GetByPaymentSessionIdAsync(notification.PaymentSessionId)`
3. `Order.PaymentSessionId` เป็น NULL เสมอ — write site มี 2 จุดและตายทั้งคู่: ctor param (`Order.cs:60`) ที่ทุก caller ส่ง null (`CheckoutConfirmedConsumer.cs:38-40`, `CreateOrderCommand.cs:43-44`) และ `AttachPaymentSession` (`Order.cs:94-101`) ที่มี **0 callers**
4. lookup คืน null → null-branch (`OrderPaidConsumer.cs:35-41`) return เงียบ → `OutboxDispatcher.cs:124` mark `Processed` → `Order.MarkPaid` (`Order.cs:109-125`) ไม่เคยถูกเรียก

Repro ที่รันได้จริง (unit-level — จะเป็น repro test RED ของ fix นี้):

- สร้าง order แบบเดียวกับ production path: `Order.Create(tenantId, amount, checkoutSessionId: X)` (ไม่มี `paymentSessionId` — เหมือน `CheckoutConfirmedConsumer.cs:38-40`) ใส่ fake repository (`tests/Orders.Tests/Fakes.cs`)
- ส่ง `PaymentPaid(PaymentSessionId: S ใดๆ, OrderId: order.Id, TenantId, Amount: ยอดตรง, ...)` เข้า `OrderPaidConsumer.Handle`
- วัดผล: `order.Status == AwaitingPayment`, `PaidAt == null`, ไม่มี `OrderPaid` domain event — ทั้งที่ควรเป็น `Paid`
- คำสั่ง: `dotnet test tests/Orders.Tests` — ปัจจุบัน 20/20 เขียวเพราะ**ไม่มี test ใด reference `OrderPaidConsumer` เลย** (test-gap ยืนยันโดยรันจริง)

Spec ที่ถูกละเมิด: foundation-scaffold REQ-9.5 (Orders รับ `PaymentPaid` ต้อง verify amount+currency
แล้วเปลี่ยนเป็น Paid — ไม่เคยเกิดขึ้นจริง), REQ-9 (webhook ที่ verify แล้วต้องทำให้ Order เป็น Paid),
system-completion loop แกน `...→ PaymentPaid → Paid` (requirements.md:5)

## Expected Behavior

- F1  WHEN `PaymentPaid` มาถึงและ `OrderId` ชี้ order ที่มีอยู่ใน tenant เดียวกัน สถานะ
      `AwaitingPayment` และ amount+currency ตรงกับ order THE SYSTEM SHALL เปลี่ยน order เป็น
      `Paid`, บันทึก `PaidAt`, และ raise domain event `OrderPaid` หนึ่งครั้ง
- F2  WHEN `PaymentPaid` มาถึง THE SYSTEM SHALL ระบุ order เป้าหมายจาก `PaymentPaid.OrderId`
      (field ชั้นหนึ่งของ contract ตาม foundation-scaffold REQ-2.2) — ไม่พึ่ง
      `Order.PaymentSessionId` ที่ไม่มีเส้นทางเขียนค่าใน production
- F3  WHEN `PaymentPaid` มี amount หรือ currency ไม่ตรงกับ order THE SYSTEM SHALL ไม่เปลี่ยน
      สถานะ order และปล่อยให้ message ล้มเหลวแบบมีร่องรอย (`MarkFailed` → retry ตาม
      `MaxAttempts` → DLQ/poison) — ห้าม ack เงียบ
- F4  WHEN order เป้าหมายอยู่สถานะ `Cancelled` แล้ว `PaymentPaid` มาถึง THE SYSTEM SHALL ไม่
      เปลี่ยนสถานะ order และปล่อยให้ message เข้า DLQ/poison เช่นเดียวกับ F3 (เงินจริงถูกจ่าย
      บน order ที่ยกเลิก = anomaly ต้องมีคนเห็น)

## Unchanged Behavior

- B1  WHEN `PaymentPaid` ซ้ำ (redelivery/replay) มาถึง order ที่เป็น `Paid` แล้ว THE SYSTEM
      SHALL CONTINUE TO no-op แบบ idempotent — สถานะคง `Paid`, ไม่ raise `OrderPaid` ซ้ำ,
      message ack ปกติ (`Order.cs:111-112`)
- B2  WHEN `PaymentPaid` อ้าง `OrderId` ที่ไม่มีในโมดูลนี้ (foreign/unknown — at-least-once
      delivery) THE SYSTEM SHALL CONTINUE TO ack-and-return โดยไม่ throw (ไม่ poison
      dispatcher) (`OrderPaidConsumer.cs:35-41` เจตนาเดิมของ null-branch)
- B3  WHEN worker consume `CheckoutConfirmed` THE SYSTEM SHALL CONTINUE TO สร้าง order เดียว
      ต่อ `CheckoutSessionId` (idempotent ผ่าน filtered-unique index) พร้อม enqueue
      `CustomerOrderNotification` เมื่อมี recipient (`CheckoutConfirmedConsumer.cs:33-40`)
- B4  WHEN PSP webhook เข้ามา THE SYSTEM SHALL CONTINUE TO ทำ pipeline ฝั่ง Payments ครบเดิม
      ใน transaction เดียว: verify signature → claim multi-key idempotency → fetch-to-confirm
      → `PaymentSession.MarkPaid` → enqueue `PaymentPaid` (`HandlePspWebhookHandler.cs`) —
      fix นี้ไม่แตะฝั่ง Payments
- B5  WHEN `PaymentPaid` ถูก serialize/deserialize ผ่าน outbox THE SYSTEM SHALL CONTINUE TO
      รักษา `OrderId` + `Money` (amount+currency) ครบถ้วนในรูป camelCase (`OutboxSerializerTests.cs`)
- B6  WHEN เรียก reconciliation report THE SYSTEM SHALL CONTINUE TO aggregate เฉพาะ order
      ของ tenant ที่ scope ไว้ (RLS floor) (`OrdersReconciliationIntegrationTests.cs`)

หมายเหตุ scope:
- ไม่มี do-not-modify list (intake ข้อ 4: "ไม่มีข้อห้ามพิเศษ")
- นอก scope (บันทึกไว้ ไม่แก้ในนี้): `POST /payment-sessions` ไม่ validate ว่า order มีจริง/amount
  ตรง (`Program.cs:665-674`, `CreatePaymentSessionHandler.cs:31-41`) — เป็น validation gap แยก
  ที่ F3 ทำให้มองเห็นผ่าน DLQ แทนที่จะเงียบ; แก้จริงควรเปิด spec ของตัวเอง
