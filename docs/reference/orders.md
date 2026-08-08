# Orders Module Reference

> As-built 2026-08-07. Order เกิดตรงจาก Cart; ไม่มี Checkout หรือ policy surface.

## Creation

`POST /api/v1/orders` รับ `cartId`, optional customer และ optional claimed `amount`. Server revalidate source,
ใช้ server-owned Cart price/metadata แล้ว host coordinator commit transaction เดียว:

1. reload Cart ของ merchant + ตรวจ `Open`/Version/lines
2. allocate `OrderNo` จาก `shop.OrderNoSeq`
3. insert Order + immutable items
4. enqueue customer notification
5. mark Cart `CheckedOut`

ผลสำเร็จ `201`, `Location`, `DirectOrderResult { orderId, orderNo, status, amount }`. Empty/mismatch `400`,
missing `404`, no SaleCode `403`, concurrency/state `409`, dependency `503`.

## Aggregate

Order owns customer/contact scalars, SaleCode, amount, status, summary capability and item snapshots. Item fields:
`ProductCode`, `VariantCode`, `VariantName`, quantity, unit price, zero discount and typed PII-free metadata.
Generic summary/listไม่คืน metadataหรือ customer PII. Merchant detail reveal audited and fail-closed.

Status: `Pending`, `Paid`, `Failed`, `Expired`, `Refunded`, `Cancelled`.

## Payment lifecycle

Versioned events: `PaymentPaid`, `PaymentFailed`, `PaymentExpired`. All lifecycle writers acquire tenant-scoped Order
row lock. Event correlation prevents stale attempt overwrite. First valid Paid wins; conflicting Paid after terminal state
creates reconciliation-required evidence. Failed/Expired allow controlled retry. Cancel refuses chargeable/settled/unknown
PSP state.

## Persistence

- `shop.Orders`
- `shop.OrderItems`
- `shop.OrderItemRevealAudits`
- `shop.OrderNoSeq` raw sequence
- owner ports `IOrderRepository`, `IOrderStore`, `IOrderSummaryReader`
- shared write seams limited by architecture allowlist

Retired: `CheckoutSessionId`, Checkout consumer/event, ItemPolicy/PolicyAudit, policy reports/routes and their grants.

Consumer examples/error map: `.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`.
