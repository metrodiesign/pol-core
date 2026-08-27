# Implementation Tasks: System Completion
> Status: unknown

- [x] 1. Cart management — `Cart.SetItemQuantity` (domain) + `GetCart` query (CartView), `RemoveItemCommand`, `SetItemQuantityCommand`, `ClearCartCommand` handlers + host endpoints (GET /carts/{id}, DELETE/PUT /carts/{id}/items/{productId}, POST /carts/{id}/clear, RequireAuthorization("tenant")). Done = domain + handler unit tests green; not-open -> 409, qty<=0 -> 400.
     REQ-1.1/1.2/1.3/1.4/1.5/1.6/1.7.

- [x] 2. Order summary link + TTL — `Order` gains SummaryToken + expiry (Create issues, default 72h; ReissueSummary rotates+extends), `GetOrderSummaryByTokenQuery` (404 unknown / 410 expired / view), `ResendOrderSummaryCommand`; new `GoneException` -> 410 in ProblemDetailsExceptionHandler; host GET /orders/{token}/summary (public, token = capability) + POST /orders/{id}/summary/resend (tenant). Done = domain + handler tests green.
     REQ-2.1/2.2/2.3/2.4/2.5/2.6. Depends on: -.

- [x] 3. Customer notification via outbox — `Contracts.CustomerOrderNotification`, enqueue in CreateOrder + ResendOrderSummary (same UoW), `INotificationSender` port + `LoggingNotificationSender` default (no PII), Worker consumer `INotificationHandler<CustomerOrderNotification>` building the link + calling the sender; relies on existing OutboxDispatcher retry/DLQ. CreateOrderCommand gains optional Recipient. Done = consumer test (calls sender; failure propagates for retry) + enqueue test.
     REQ-3.1/3.2/3.3/3.4/3.5. Depends on: 2.

- [x] 4. Reconciliation reporting — `GetReconciliationSummaryQuery` + handler (group bound tenant's orders by Status+Currency -> count + total minor units), `ReconciliationView`; host GET /reports/reconciliation (tenant). Done = handler test (groups correctly, per-currency) + integration (tenant-scoped).
     REQ-4.1/4.2/4.3. Depends on: -.

- [x] 5. Checkout -> Order keystone — `CheckoutSession` + NotificationRecipient (StartCheckout captures); `Order` + CheckoutSessionId + filtered unique index (idempotency key); `Contracts.CheckoutConfirmed`; ConfirmCheckout enqueues it (same UoW); Orders `CheckoutConfirmedConsumer` (skip if order exists for the session, else create order + notify); OutboxDispatcher case; host POST /checkout + POST /checkout/{id}/confirm; migration AddOrderCheckoutSession. Done = consumer idempotency test + confirm-emits test + build.
     REQ-5.1/5.2/5.3/5.4/5.5. Depends on: 3.
- Notification rides the existing transactional outbox + Worker OutboxDispatcher (retry/backoff/DLQ already there); real email/SMS provider deferred (logging default impl, swappable via DI).
