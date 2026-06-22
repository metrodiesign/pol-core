# Implementation Tasks: System Completion
> Status: approved 2026-06-23 (autonomous, AFK). Branch feat/system-completion (off develop). Commit per task; PR not merged.

- [x] 1. Cart management — `Cart.SetItemQuantity` (domain) + `GetCart` query (CartView), `RemoveItemCommand`, `SetItemQuantityCommand`, `ClearCartCommand` handlers + host endpoints (GET /carts/{id}, DELETE/PUT /carts/{id}/items/{productId}, POST /carts/{id}/clear, RequireAuthorization("tenant")). Done = domain + handler unit tests green; not-open -> 409, qty<=0 -> 400.
     Satisfies: REQ-1.1/1.2/1.3/1.4/1.5/1.6/1.7. Verify: `dotnet test tests/Cart.Tests`.
     Evidence: `dotnet test tests/Cart.Tests` -> Passed 15 (domain: AddItem-merge/Subtotal, SetItemQuantity set/reject<=0/reject-unknown-product/reject-not-open, Remove, Clear; handlers: GetCart own-tenant view / null wrong-tenant+missing, Remove+save, SetQuantity update/reject<=0, Clear, edit-on-missing rejected). Api build + Hosts.Tests 39 green (container boots with new handlers/endpoints). Endpoints added: POST /carts, POST /carts/{id}/items, GET /carts/{id} (404 if null), DELETE/PUT /carts/{id}/items/{productId}, POST /carts/{id}/clear (all RequireAuthorization("tenant")). Cart/Checkout/Orders had NO host endpoints on develop — Cart now reachable. Viewports: n/a. Deviations: edit on missing cart -> InvalidOperationException (409), mirroring existing AddItemToCartHandler for module consistency (not 404). New Cart.Tests project + slnx entry.

- [ ] 2. Order summary link + TTL — `Order` gains SummaryToken + expiry (Create issues, default 72h; ReissueSummary rotates+extends), `GetOrderSummaryByTokenQuery` (404 unknown / 410 expired / view), `ResendOrderSummaryCommand`; new `GoneException` -> 410 in ProblemDetailsExceptionHandler; host GET /orders/{token}/summary (public, token = capability) + POST /orders/{id}/summary/resend (tenant). Done = domain + handler tests green.
     Satisfies: REQ-2.1/2.2/2.3/2.4/2.5. Depends on: -. Verify: `dotnet test tests/Orders.Tests`.

- [ ] 3. Customer notification via outbox — `Contracts.CustomerOrderNotification`, enqueue in CreateOrder + ResendOrderSummary (same UoW), `INotificationSender` port + `LoggingNotificationSender` default (no PII), Worker consumer `INotificationHandler<CustomerOrderNotification>` building the link + calling the sender; relies on existing OutboxDispatcher retry/DLQ. CreateOrderCommand gains optional Recipient. Done = consumer test (calls sender; failure propagates for retry) + enqueue test.
     Satisfies: REQ-3.1/3.2/3.3/3.4/3.5. Depends on: 2. Verify: `dotnet test tests/Orders.Tests`.

- [ ] 4. Reconciliation reporting — `GetReconciliationSummaryQuery` + handler (group bound tenant's orders by Status+Currency -> count + total minor units), `ReconciliationView`; host GET /reports/reconciliation (tenant). Done = handler test (groups correctly, per-currency) + integration (tenant-scoped).
     Satisfies: REQ-4.1/4.2/4.3. Depends on: -. Verify: `dotnet test tests/Orders.Tests`.

## Notes
- No new module; extends Cart + Orders. Domains stay EF-free; ITenantScoped + RLS preserved.
- Notification rides the existing transactional outbox + Worker OutboxDispatcher (retry/backoff/DLQ already there); real email/SMS provider deferred (logging default impl, swappable via DI).
