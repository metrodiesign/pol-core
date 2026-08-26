# Design: System Completion
> Status: unknown

## Architecture Overview
ต่อยอด 5 business module เดิม — ไม่มีโมดูลใหม่. ทุก command/query ผ่าน Mediator, ITenantScoped, RLS เดิม. Notification ขี่ transactional outbox (`IOutbox`/`OutboxDispatcher`) + Worker host ที่มีอยู่.

## Slice 1 — Cart management (Cart.Application + host)
Domain พร้อม (`Cart.RemoveItem`, `Clear`, `Subtotal`; ต้องเพิ่ม `SetItemQuantity(productId, qty)`). เพิ่ม:
- `GetCart/GetCartQuery` (+ `CartView` { CartId, Status, Items[], Subtotal? }) + handler (load by id, tenant-scoped).
- `RemoveItemCommand` + handler (load -> RemoveItem -> save).
- `SetItemQuantityCommand` + handler (load -> SetItemQuantity -> save; qty<=0 -> ArgumentException -> 400).
- `ClearCartCommand` + handler.
- `ICartRepository` already exposes `GetAsync`. Host endpoints: `GET /carts/{id}`, `DELETE /carts/{id}/items/{productId}`, `PUT /carts/{id}/items/{productId}` (body qty), `POST /carts/{id}/clear` — all RequireAuthorization("tenant"). Cart-not-open -> InvalidOperationException -> 409.

## Slice 2 — Order summary link + TTL (Orders.Domain + Application + host)
- `Order` gains `SummaryToken` (string, opaque) + `SummaryTokenExpiresAtUtc`; `Order.Create` issues them (TTL param, default 72h); `ReissueSummary(newToken, now, ttl)` rotates + extends. Token = `Guid.NewGuid("N")` (opaque, unguessable enough for a captive link; ponytail — not a secret, just unguessable).
- `GetOrderSummaryByToken/Query` -> handler: load by token; null -> NotFoundException (404); expired (now >= expiry) -> a `GoneException` (new, maps 410); else `OrderSummaryView`.
- `ResendOrderSummaryCommand` -> handler: load by id (tenant-scoped) -> ReissueSummary -> save -> returns new token. (Enqueue handled in slice 3.)
- New `GoneException` (BuildingBlocks.Application) + map 410 in ProblemDetailsExceptionHandler.
- Host: `GET /orders/{token}/summary` (anonymous — customer link, token IS the capability; no tenant binding -> read via admin/bypass? No: summary read is tenant-scoped data. Token lookup must run WITHOUT a tenant bound. Decision: the public summary endpoint resolves the order by token on a bypass/worker connection like the webhook resolver, then returns a minimal view. ponytail: reuse the pattern — a dedicated read that is NOT ITenantScoped, reading only the one order the token names.) `POST /orders/{id}/summary/resend` (RequireAuthorization("tenant")).

## Slice 3 — Customer notification (Contracts + Orders + Worker)
- `Contracts.CustomerOrderNotification` (INotification): { TenantId, OrderId, Recipient, SummaryToken, OccurredAtUtc }.
- `CreateOrderHandler` + `ResendOrderSummaryHandler`: after building/rotating, `IOutbox.Enqueue(new CustomerOrderNotification(...))` in the same UoW (REQ-3.1).
- `INotificationSender` port (BuildingBlocks.Application or a new Notifications seam) — `Task SendAsync(NotificationMessage, ct)`. Default `LoggingNotificationSender` (logs a non-PII line: order id + that a link was sent; NOT the recipient address). Wire in Worker host.
- Worker consumer: an `INotificationHandler<CustomerOrderNotification>` that builds the link + calls the sender. OutboxDispatcher already provides retry/backoff/DLQ (MaxAttempts) — a throw from the sender is retried (REQ-3.4).
- CreateOrderCommand needs a `Recipient` (email/phone). Add optional `Recipient` to the command (Checkout supplies it per reference line 33 "ตั้งค่าผู้รับแจ้งเตือน"). If absent, no notification enqueued.

## Slice 4 — Reconciliation reporting (Orders.Application + host)
- `GetReconciliationSummary/Query` -> handler: group the bound tenant's orders by (Status, Currency) -> counts + summed minor units. Read-only, ITenantScoped.
- `ReconciliationView` { Lines: [{ Status, Currency, Count, TotalMinorUnits }] }.
- Host: `GET /reports/reconciliation` (RequireAuthorization("tenant")).

## Slice 5 — Checkout confirms into an order (keystone)
Wires the deferred Checkout->Order seam via an integration event (mirrors PaymentPaid), keeping the modules decoupled:
- `CheckoutSession` + `NotificationRecipient` (nullable); `StartCheckout` captures it. `Order` + `CheckoutSessionId` (nullable) + filtered UNIQUE index — the idempotency key.
- `Contracts.CheckoutConfirmed` (INotification): { TenantId, CheckoutSessionId, AmountMinorUnits, Currency, Recipient?, OccurredAtUtc }.
- `ConfirmCheckoutHandler`: on `Confirm()`, `IOutbox.Enqueue(CheckoutConfirmed)` in the same UoW.
- Orders `CheckoutConfirmedConsumer` (INotificationHandler): if `GetByCheckoutSessionIdAsync` finds an order, SKIP (REQ-5.3 idempotent); else `IMediator.Send(CreateOrderCommand{ CheckoutSessionId, Recipient })` — reusing CreateOrderHandler, which enqueues the notification (REQ-5.4).
- `OutboxDispatcher` switch gains a `CheckoutConfirmed` case.
- Host: `POST /checkout` (start, with recipient) + `POST /checkout/{id}/confirm` (tenant).
- Migration `AddOrderCheckoutSession`: Order.CheckoutSessionId + filtered unique index; CheckoutSession.NotificationRecipient.

## Error Handling Strategy
Reuse `ProblemDetailsExceptionHandler`: ArgumentException->400, Conflict/InvalidOperationException->409, NotFoundException->404, new GoneException->410. No new 5xx paths.

## Testing Strategy
- Pure-domain first: Cart.SetItemQuantity (positive/zero/not-open), Order summary token issue/expiry/reissue. (xUnit, fakes.)
- Application: handler tests with fakes (GetCart maps view; RemoveItem/Clear; SetQuantity rejects <=0; summary-by-token 404/410/ok; resend rotates + enqueues; reconciliation groups by status+currency; notification consumer calls sender + propagates failure for retry).
- Architecture: no new cross-module deps; domains stay EF-free.
- Integration ([Trait Integration]): summary-by-token read returns only the named order; reconciliation scoped to tenant.

## Requirement Traceability
| Design element | REQ |
|---|---|
| Slice 1 Cart handlers + endpoints | REQ-1.1–1.7 |
| Slice 2 Order summary token + TTL + GoneException | REQ-2.1–2.6 |
| Slice 3 outbox enqueue + INotificationSender + Worker consumer | REQ-3.1–3.5 |
| Slice 4 reconciliation query | REQ-4.1–4.3 |
| Slice 5 CheckoutConfirmed event + idempotent consumer -> CreateOrder | REQ-5.1–5.5 |
