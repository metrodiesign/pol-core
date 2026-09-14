# pol-core API — Activity และ Sequence Diagrams ของทุก endpoint

> Source: `docs/reference/api-endpoints.md` ณ 2026-09-14
> Scope: 249 endpoints ใน 14 themes + cross-cutting
> Generated: 2026-09-14

---

## วิธีอ่าน

เอกสารชุดนี้แบ่งเป็น 14 theme (`01`–`14`) บวก 1 ไฟล์ cross-cutting (`00`) แต่ละ theme มี 2 ไฟล์คู่กัน: `NN-slug.activities.md` (activity diagram, `flowchart TD`) และ `NN-slug.sequences.md` (sequence diagram, `sequenceDiagram`) หมายเลข `§ N.k` ตรงกันเป๊ะระหว่างสองไฟล์ — หัวข้อ `## 4.2 ...` ใน activities.md คือ flow เดียวกับ `## 4.2 ...` ใน sequences.md

คอลัมน์ "รูปแบบ" ในตาราง coverage มี 2 ค่า:

- `diagram` — endpoint เป็น subject ของ § นั้นโดยตรง มีทั้ง activity diagram และ sequence diagram เต็มรูปให้
- `composed` — endpoint โครงเดียวกับ endpoint อื่นในกลุ่ม (list/get/create/update/delete ที่ต่างแค่ permission, entity, validation) จึงอยู่ในตาราง "flow ประกอบ" ใต้ § กลาง แทนที่จะวาดซ้ำ พร้อมบอกว่าต่างจาก § กลางตรงไหน

ทุก theme อ้างพฤติกรรมร่วม (auth/CSRF/rate-limit/ETag/idempotency/SFS/maker-checker/outbox/error contract) กลับไปที่ `§ 0.x` ใน `00-cross-cutting.*` แทนการวาดซ้ำ — ดูตาราง Cross-cutting ด้านล่าง

---

## Theme index

| # | Theme | Endpoints | § | Activities | Sequences |
| --- | --- | --- | --- | --- | --- |
| 01 | Identity platform login และ OAuth | 14 | 1.1–1.5, 1.7–1.9 | [activities](01-identity-bff-oauth.activities.md) | [sequences](01-identity-bff-oauth.sequences.md) |
| 02 | Canonical access, roles และ SYSTEM clients | 22 | 2.1–2.6 | [activities](02-canonical-access-system-clients.activities.md) | [sequences](02-canonical-access-system-clients.sequences.md) |
| 03 | Agent registration (ผู้สมัครและผู้ตรวจ) | 9 | 3.1–3.6 | [activities](03-agent-registration.activities.md) | [sequences](03-agent-registration.sequences.md) |
| 04 | Carts, products และ order lifecycle | 18 | 4.1–4.10 | [activities](04-carts-products-orders.activities.md) | [sequences](04-carts-products-orders.sequences.md) |
| 05 | Customer checkout, payment sessions และ PSP callbacks | 17 | 5.1–5.10 | [activities](05-customer-checkout-payment-psp.activities.md) | [sequences](05-customer-checkout-payment-psp.sequences.md) |
| 06 | Canonical commerce และ transactions | 9 | 6.1–6.7 | [activities](06-canonical-commerce-transactions.activities.md) | [sequences](06-canonical-commerce-transactions.sequences.md) |
| 07 | Canonical control plane (merchant และ provider configuration) | 24 | 7.1–7.6 | [activities](07-canonical-control-plane.activities.md) | [sequences](07-canonical-control-plane.sequences.md) |
| 08 | Admin OIDC callback (บัญชี/role/session ย้ายไป § 1, 2, 3) | 1 | 8.1 | [activities](08-admin-accounts-auth.activities.md) | [sequences](08-admin-accounts-auth.sequences.md) |
| 09 | Merchant provisioning, merchant users และ merchant OIDC | 25 | 9.1–9.8 | [activities](09-merchant-provision-users-auth.activities.md) | [sequences](09-merchant-provision-users-auth.sequences.md) |
| 10 | Admin merchant console, merchant roles และ originators | 21 | 10.1–10.10 | [activities](10-admin-merchant-console-originators.activities.md) | [sequences](10-admin-merchant-console-originators.sequences.md) |
| 11 | Payment capability configuration (methods, providers, PSP connections, routing) | 39 | 11.1–11.10 | [activities](11-payment-capability-config.activities.md) | [sequences](11-payment-capability-config.sequences.md) |
| 12 | Governance (maker-checker), audit และ API clients | 13 | 12.1–12.7 | [activities](12-governance-audit-api-clients.activities.md) | [sequences](12-governance-audit-api-clients.sequences.md) |
| 13 | Notifications และ outbound webhooks | 24 | 13.1–13.9 | [activities](13-notifications-outbound-webhooks.activities.md) | [sequences](13-notifications-outbound-webhooks.sequences.md) |
| 14 | Inbound PSP webhook log, reporting, health และ development surfaces | 13 | 14.1–14.7 | [activities](14-inbound-webhooks-reporting-infra.activities.md) | [sequences](14-inbound-webhooks-reporting-infra.sequences.md) |

---

## Cross-cutting

พฤติกรรมร่วมที่ใช้ซ้ำข้ามหลาย theme บันทึกไว้ครั้งเดียวใน `00-cross-cutting.*` — theme อื่นอ้าง `§ 0.x` แทนการวาด gate/pipeline ซ้ำ

| § | เรื่อง | สรุป | ลิงก์ |
| --- | --- | --- | --- |
| 0.1 | Console session authentication + authorization | อ้าง § 0.1 เมื่อแถว endpoint ใช้ policy admin (Bearer PlatformToken) / merchant-user (cookie __Host-mch_session) / dual-console และมี RequirePermission, RequireAudiencePermission (dual-console admin key / merchant key), RequirePlatformUserTier(Super) หรือ BoundFilter — ครอบ 401 invalid_token (admin Bearer), 401 default (merchant), 403 lifecycle code (awaiting-approval / rejected / suspended / unbound), 403 permission, 403 super_required | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.2 | Identity platform (Bearer) authentication + identity-order permission | อ้าง § 0.2 เมื่อแถว endpoint ใช้ policy identity-platform / admin-or-identity-order หรือมี (identity: order.read / order.write / checkout.write) — ครอบ UseIdentityAccess 400 ambiguous_authentication_context, OpenIddict Bearer validation + AuthorizationVersion, IdentityAccessRequirement 403, RequireOrderIdentityPermission / RequireIdentityPermission (human permission vs system scope, 403 merchant_context_missing), CommerceAuthorizationProof + IActorScope binding — ไม่มี BFF cookie แล้ว | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.3 | CSRF double-submit | อ้าง § 0.3 เมื่อแถว endpoint (merchant-user cookie) ระบุ CSRF บน unsafe method (RequireUserCsrf mch_csrf, RequireAudienceCsrf ฝั่ง Merchant) — admin เป็น Bearer จึงไม่มี CSRF; RequireIdentityPlatformMutation / RequireAdminOrIdentityMutation ตรวจแค่ว่ามี Bearer (401 bearer_required) — ครอบ 403 Missing or invalid CSRF token และ boot guard CsrfParity | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.4 | Rate limiting | อ้าง § 0.4 เมื่อแถว endpoint ระบุ rate limit: customer-payment (checkout*, orders/{token}/pay, payment-status, payment-returns), merchant-user-auth (merchants/users/register, merchants/auth login + logout), psp-webhook (webhooks/{pspConnectionId}, webhooks/payment-providers/{id}) — 429 + Retry-After ก่อน authentication | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.5 | ETag / If-Match / Idempotency-Key | อ้าง § 0.5 เมื่อ endpoint มี IfMatchMutationMarker, AdminIfMatchMutationMarker / AdminIdempotencyMutationMarker (บังคับเฉพาะ audience Admin), IdempotencyMutationMarker, GovernanceDecisionMarker หรือ GET detail ที่คืน ETag vN — ครอบ 400 invalid_etag / invalid_idempotency_key, 409 idempotency_key_reused / operation_in_progress / ConcurrencyConflict และ replay response เดิม | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.6 | SFS query parsing | อ้าง § 0.6 เมื่อ GET list อ่าน page / limit / filters / sort / search ผ่าน SfsQueryParser (SfsQueryParamsMarker) — clamp paging ไม่ 400, JSON ผิดหรือเกิน cap (50 filters / 200 values / 10 sort keys) เป็น 400 Invalid request, variant products (ParsePaging + productFilters) และ approvals / audits (typed filter, 400 invalid_filter) | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.7 | Maker-checker approval | อ้าง § 0.7 เมื่อ endpoint ลงท้าย -requests, -change-requests, activation-requests, secret-rotation-requests (maker: 202 pending + approvalId ผ่าน ControlPlaneOperationExecutor + outbox ApprovalRequested) หรือเป็น /approvals/{approvalId}/approve / reject (checker: If-Match + Idempotency-Key, 400 invalid_request, 403 maker_cannot_decide / merchant_scope_forbidden / underlying_permission_forbidden, 404 not_found, 409 approval_not_pending / target_version_changed, 202 + ETag) และ execution async ผ่าน GovernanceOutboxDispatcher + IApprovalDecisionExecutor + ApprovalExecutionReported | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.8 | Background dispatch / outbox | อ้าง § 0.8 เมื่อ handler enqueue outbox (IOutbox.Enqueue ใน transaction เดียว) หรือทิ้งงานให้ worker: OutboxDispatcher (txn.OutboxMessages, PaymentPaid ฯลฯ), MerchantUserOutboxDispatcher, GovernanceOutboxDispatcher, NotificationDeliveryDispatcher (txn.Deliveries), WebhookDeliveryDispatcher (admin.WebhookDeliveries), TransactionInquiryWorker (PSP inquiry) — lease / at-least-once / MaxAttempts 8, WorkerActorContext + WorkerWriteAuthorizer ใน background scope | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |
| 0.9 | Error contract | อ้าง § 0.9 สำหรับรูป error ทุก endpoint: ProblemDetails JSON (Results.Problem หรือ ProblemDetailsExceptionHandler map NotFound 404, Gone 410, Conflict / ConcurrencyConflict / InvalidOperation 409, AccessDenied 403, InvalidRequest / Argument 400, Upstream / DependencyUnavailable 503, อื่น 500 + code + traceId), OIDC callback 302 ไป returnTo (ReturnUrlPolicy) หรือ error page ?reason=code, PSP browser return 303 Location /api/v1/checkout/status หรือ 401 checkout_return_invalid, PSP webhook 200 / 202 / 401 / 404 / 503 | [activities](00-cross-cutting.activities.md) · [sequences](00-cross-cutting.sequences.md) |

---

## Coverage matrix

ทุก endpoint จาก inventory (docs/reference/api-endpoints.md) เรียงตาม theme แล้วตาม path — รวม 249 แถว (245 mapped operation + infra 2 + OIDC callback 2)

| Method | fullPath | Theme | § | รูปแบบ |
| --- | --- | --- | --- | --- |
| GET | `/.well-known/jwks.json` | 01 | [1.1](01-identity-bff-oauth.activities.md) | composed |
| GET | `/.well-known/oauth-authorization-server` | 01 | [1.1](01-identity-bff-oauth.activities.md) | diagram |
| GET | `/api/v1/auth/agents/callback` | 01 | [1.4](01-identity-bff-oauth.activities.md) | diagram |
| GET | `/api/v1/auth/agents/login` | 01 | [1.2](01-identity-bff-oauth.activities.md) | composed |
| GET | `/api/v1/auth/employees/callback` | 01 | [1.3](01-identity-bff-oauth.activities.md) | diagram |
| POST | `/api/v1/auth/logout` | 01 | [1.7](01-identity-bff-oauth.activities.md) | diagram |
| GET | `/api/v1/me` | 01 | [1.5](01-identity-bff-oauth.activities.md) | diagram |
| GET | `/api/v1/me/access` | 01 | [1.5](01-identity-bff-oauth.activities.md) | composed |
| GET | `/api/v1/me/merchants` | 01 | [1.5](01-identity-bff-oauth.activities.md) | composed |
| GET | `/api/v1/me/sessions` | 01 | [1.5](01-identity-bff-oauth.activities.md) | composed |
| DELETE | `/api/v1/me/sessions/{sessionId}` | 01 | [1.7](01-identity-bff-oauth.activities.md) | composed |
| GET | `/oauth/authorize` | 01 | [1.9](01-identity-bff-oauth.activities.md) | diagram |
| POST | `/oauth/revoke` | 01 | [1.9](01-identity-bff-oauth.activities.md) | composed |
| POST | `/oauth/token` | 01 | [1.8](01-identity-bff-oauth.activities.md) | diagram |
| GET | `/api/v1/accounts` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/accounts/{accountId:guid}` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| PATCH | `/api/v1/accounts/{accountId:guid}` | 02 | [2.3](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/accounts/{accountId:guid}/merchant-access` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| DELETE | `/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}` | 02 | [2.3](02-canonical-access-system-clients.activities.md) | composed |
| PUT | `/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}` | 02 | [2.3](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/accounts/{accountId:guid}/platform-access` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| PUT | `/api/v1/accounts/{accountId:guid}/platform-access` | 02 | [2.3](02-canonical-access-system-clients.activities.md) | composed |
| POST | `/api/v1/accounts/{accountId:guid}/session-revocations` | 02 | [2.3](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/permissions` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/roles` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| POST | `/api/v1/roles` | 02 | [2.4](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/roles/{roleId:guid}` | 02 | [2.1](02-canonical-access-system-clients.activities.md) | composed |
| PUT | `/api/v1/roles/{roleId:guid}` | 02 | [2.4](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/system-clients` | 02 | [2.2](02-canonical-access-system-clients.activities.md) | composed |
| POST | `/api/v1/system-clients` | 02 | [2.5](02-canonical-access-system-clients.activities.md) | diagram |
| GET | `/api/v1/system-clients/{clientId:guid}` | 02 | [2.2](02-canonical-access-system-clients.activities.md) | composed |
| PATCH | `/api/v1/system-clients/{clientId:guid}` | 02 | [2.6](02-canonical-access-system-clients.activities.md) | composed |
| PUT | `/api/v1/system-clients/{clientId:guid}/access` | 02 | [2.6](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/system-clients/{clientId:guid}/keys` | 02 | [2.2](02-canonical-access-system-clients.activities.md) | composed |
| POST | `/api/v1/system-clients/{clientId:guid}/keys` | 02 | [2.6](02-canonical-access-system-clients.activities.md) | composed |
| DELETE | `/api/v1/system-clients/{clientId:guid}/keys/{keyId:guid}` | 02 | [2.6](02-canonical-access-system-clients.activities.md) | composed |
| GET | `/api/v1/agent-registration` | 03 | [3.1](03-agent-registration.activities.md) | diagram |
| PUT | `/api/v1/agent-registration` | 03 | [3.2](03-agent-registration.activities.md) | diagram |
| GET | `/api/v1/agent-registration/history` | 03 | [3.1](03-agent-registration.activities.md) | composed |
| POST | `/api/v1/agent-registration/submissions` | 03 | [3.3](03-agent-registration.activities.md) | diagram |
| GET | `/api/v1/agent-registrations` | 03 | [3.4](03-agent-registration.activities.md) | diagram |
| GET | `/api/v1/agent-registrations/{registrationId:guid}` | 03 | [3.5](03-agent-registration.activities.md) | diagram |
| GET | `/api/v1/agent-registrations/{registrationId:guid}/attempts` | 03 | [3.5](03-agent-registration.activities.md) | composed |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve` | 03 | [3.6](03-agent-registration.activities.md) | diagram |
| POST | `/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject` | 03 | [3.6](03-agent-registration.activities.md) | composed |
| POST | `/api/v1/carts` | 04 | [4.1](04-carts-products-orders.activities.md) | diagram |
| GET | `/api/v1/carts/{cartId:guid}` | 04 | [4.1](04-carts-products-orders.activities.md) | composed |
| POST | `/api/v1/carts/{cartId:guid}/clear` | 04 | [4.2](04-carts-products-orders.activities.md) | composed |
| POST | `/api/v1/carts/{cartId:guid}/items` | 04 | [4.2](04-carts-products-orders.activities.md) | diagram |
| DELETE | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | 04 | [4.2](04-carts-products-orders.activities.md) | composed |
| PUT | `/api/v1/carts/{cartId:guid}/items/{itemId:guid}` | 04 | [4.2](04-carts-products-orders.activities.md) | composed |
| GET | `/api/v1/orders` | 04 | [4.3](04-carts-products-orders.activities.md) | composed |
| POST | `/api/v1/orders` | 04 | [4.4](04-carts-products-orders.activities.md) | diagram |
| GET | `/api/v1/orders/{orderId:guid}` | 04 | [4.3](04-carts-products-orders.activities.md) | diagram |
| POST | `/api/v1/orders/{orderId:guid}/cancel` | 04 | [4.7](04-carts-products-orders.activities.md) | diagram |
| POST | `/api/v1/orders/{orderId:guid}/issue` | 04 | [4.8](04-carts-products-orders.activities.md) | composed |
| GET | `/api/v1/orders/{orderId:guid}/payment-links` | 04 | [4.3](04-carts-products-orders.activities.md) | composed |
| POST | `/api/v1/orders/{orderId:guid}/payment-links` | 04 | [4.8](04-carts-products-orders.activities.md) | diagram |
| POST | `/api/v1/orders/{orderId:guid}/summary/resend` | 04 | [4.9](04-carts-products-orders.activities.md) | diagram |
| GET | `/api/v1/orders/export` | 04 | [4.6](04-carts-products-orders.activities.md) | diagram |
| POST | `/api/v1/orders/from-cart` | 04 | [4.5](04-carts-products-orders.activities.md) | diagram |
| GET | `/api/v1/products` | 04 | [4.10](04-carts-products-orders.activities.md) | diagram |
| GET | `/api/v1/products/documents` | 04 | [4.10](04-carts-products-orders.activities.md) | composed |
| POST | `/api/v1/checkout/access` | 05 | [5.1](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/checkout/confirm` | 05 | [5.2](05-customer-checkout-payment-psp.activities.md) | diagram |
| GET | `/api/v1/checkout/status` | 05 | [5.3](05-customer-checkout-payment-psp.activities.md) | diagram |
| GET | `/api/v1/checkout/summary` | 05 | [5.3](05-customer-checkout-payment-psp.activities.md) | composed |
| POST | `/api/v1/checkout/verify` | 05 | [5.2](05-customer-checkout-payment-psp.activities.md) | composed |
| POST | `/api/v1/orders/{token}/pay` | 05 | [5.4](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/orders/{token}/payment-status` | 05 | [5.4](05-customer-checkout-payment-psp.activities.md) | composed |
| GET | `/api/v1/orders/{token}/summary` | 05 | [5.4](05-customer-checkout-payment-psp.activities.md) | composed |
| POST | `/api/v1/payment-links/{linkId:guid}/revoke` | 05 | [5.5](05-customer-checkout-payment-psp.activities.md) | diagram |
| GET | `/api/v1/payment-returns/{providerCode}` | 05 | [5.6](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/payment-returns/{providerCode}` | 05 | [5.6](05-customer-checkout-payment-psp.activities.md) | composed |
| GET | `/api/v1/payments/sessions` | 05 | [5.7](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/payments/sessions` | 05 | [5.8](05-customer-checkout-payment-psp.activities.md) | diagram |
| GET | `/api/v1/payments/sessions/{paymentSessionId:guid}` | 05 | [5.9](05-customer-checkout-payment-psp.activities.md) | composed |
| POST | `/api/v1/payments/sessions/{paymentSessionId:guid}/redirect` | 05 | [5.9](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/webhooks/{pspConnectionId:guid}` | 05 | [5.10](05-customer-checkout-payment-psp.activities.md) | diagram |
| POST | `/api/v1/webhooks/payment-providers/{providerAccountId:guid}` | 05 | [5.10](05-customer-checkout-payment-psp.activities.md) | composed |
| GET | `/api/v1/checkout/payment-methods` | 06 | [6.1](06-canonical-commerce-transactions.activities.md) | diagram |
| PATCH | `/api/v1/orders/{orderId:guid}` | 06 | [6.2](06-canonical-commerce-transactions.activities.md) | diagram |
| GET | `/api/v1/orders/{orderId:guid}/history` | 06 | [6.3](06-canonical-commerce-transactions.activities.md) | composed |
| GET | `/api/v1/orders/{orderId:guid}/items` | 06 | [6.3](06-canonical-commerce-transactions.activities.md) | diagram |
| GET | `/api/v1/transactions` | 06 | [6.4](06-canonical-commerce-transactions.activities.md) | diagram |
| GET | `/api/v1/transactions/{transactionId:guid}` | 06 | [6.5](06-canonical-commerce-transactions.activities.md) | diagram |
| GET | `/api/v1/transactions/{transactionId:guid}/events` | 06 | [6.5](06-canonical-commerce-transactions.activities.md) | composed |
| POST | `/api/v1/transactions/{transactionId:guid}/review-notes` | 06 | [6.7](06-canonical-commerce-transactions.activities.md) | diagram |
| POST | `/api/v1/transactions/{transactionId:guid}/verify` | 06 | [6.6](06-canonical-commerce-transactions.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| PATCH | `/api/v1/merchants/{merchantId:guid}` | 07 | [7.2](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/branches` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/branches` | 07 | [7.2](07-canonical-control-plane.activities.md) | composed |
| PATCH | `/api/v1/merchants/{merchantId:guid}/branches/{branchId:guid}` | 07 | [7.2](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests` | 07 | [7.5](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/approve` | 07 | [7.6](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/reject` | 07 | [7.6](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/payment-settings` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts` | 07 | [7.3](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| PATCH | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}` | 07 | [7.3](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/connection-tests` | 07 | [7.4](07-canonical-control-plane.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions` | 07 | [7.5](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable` | 07 | [7.3](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/sales` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| POST | `/api/v1/merchants/{merchantId:guid}/sales` | 07 | [7.2](07-canonical-control-plane.activities.md) | composed |
| PATCH | `/api/v1/merchants/{merchantId:guid}/sales/{saleId:guid}` | 07 | [7.2](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/payment-providers` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/payment-providers/{providerId:guid}/methods` | 07 | [7.1](07-canonical-control-plane.activities.md) | composed |
| GET | `/api/v1/admins/auth/microsoft/callback` | 08 | [8.1](08-admin-accounts-auth.activities.md) | diagram |
| POST | `/api/v1/merchants` | 09 | [9.1](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/{code}` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | composed |
| GET | `/api/v1/merchants/auth/{provider}/login` | 09 | [9.2](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/auth/logout` | 09 | [9.3](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/auth/logout-all` | 09 | [9.3](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/auth/microsoft/callback` | 09 | [9.2](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | diagram |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}` | 09 | [9.7](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/approve` | 09 | [9.7](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/{merchantUserId:guid}/edit` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | composed |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reactivate` | 09 | [9.7](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/reject` | 09 | [9.7](09-merchant-provision-users-auth.activities.md) | diagram |
| PUT | `/api/v1/merchants/users/{merchantUserId:guid}/roles` | 09 | [9.8](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/users/{merchantUserId:guid}/suspend` | 09 | [9.7](09-merchant-provision-users-auth.activities.md) | diagram |
| POST | `/api/v1/merchants/users/invitations` | 09 | [9.6](09-merchant-provision-users-auth.activities.md) | diagram |
| DELETE | `/api/v1/merchants/users/invitations/{invitationId:guid}` | 09 | [9.6](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/me` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/permissions` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | composed |
| POST | `/api/v1/merchants/users/register` | 09 | [9.4](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/roles` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | composed |
| POST | `/api/v1/merchants/users/roles` | 09 | [9.8](09-merchant-provision-users-auth.activities.md) | diagram |
| DELETE | `/api/v1/merchants/users/roles/{code}` | 09 | [9.8](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants/users/roles/{code}` | 09 | [9.5](09-merchant-provision-users-auth.activities.md) | composed |
| PUT | `/api/v1/merchants/users/roles/{code}` | 09 | [9.8](09-merchant-provision-users-auth.activities.md) | diagram |
| GET | `/api/v1/merchants` | 10 | [10.1](10-admin-merchant-console-originators.activities.md) | diagram |
| PUT | `/api/v1/merchants/{merchantId:guid}` | 10 | [10.2](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}/permissions` | 10 | [10.3](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/merchants/{merchantId:guid}/reactivate` | 10 | [10.2](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}/roles` | 10 | [10.3](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/merchants/{merchantId:guid}/roles` | 10 | [10.4](10-admin-merchant-console-originators.activities.md) | diagram |
| DELETE | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | 10 | [10.4](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | 10 | [10.3](10-admin-merchant-console-originators.activities.md) | diagram |
| PUT | `/api/v1/merchants/{merchantId:guid}/roles/{code}` | 10 | [10.4](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/merchants/{merchantId:guid}/suspend` | 10 | [10.2](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/merchants/{merchantId:guid}/user-invitations` | 10 | [10.6](10-admin-merchant-console-originators.activities.md) | diagram |
| PUT | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}` | 10 | [10.5](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit` | 10 | [10.5](10-admin-merchant-console-originators.activities.md) | diagram |
| PUT | `/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles` | 10 | [10.7](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/originators` | 10 | [10.1](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/originators` | 10 | [10.8](10-admin-merchant-console-originators.activities.md) | diagram |
| DELETE | `/api/v1/originators/{originatorId:guid}` | 10 | [10.10](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/originators/{originatorId:guid}` | 10 | [10.1](10-admin-merchant-console-originators.activities.md) | diagram |
| PUT | `/api/v1/originators/{originatorId:guid}` | 10 | [10.9](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/originators/{originatorId:guid}/disable` | 10 | [10.9](10-admin-merchant-console-originators.activities.md) | diagram |
| POST | `/api/v1/originators/{originatorId:guid}/enable` | 10 | [10.9](10-admin-merchant-console-originators.activities.md) | diagram |
| GET | `/api/v1/payments/merchant-settings/{merchantId:guid}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| POST | `/api/v1/payments/merchant-settings/{merchantId:guid}/environment-change-requests` | 11 | [11.8](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing` | 11 | [11.10](11-payment-capability-config.activities.md) | diagram |
| PUT | `/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing` | 11 | [11.10](11-payment-capability-config.activities.md) | diagram |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/methods` | 11 | [11.4](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/methods/{method}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/merchants/{merchantId:guid}/methods/{method}` | 11 | [11.3](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}` | 11 | [11.3](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/options` | 11 | [11.4](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/resolution` | 11 | [11.4](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/methods` | 11 | [11.4](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/methods/{method}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/methods/{method}` | 11 | [11.2](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/methods/{method}/options` | 11 | [11.4](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/providers/{providerCode}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/providers/{providerCode}` | 11 | [11.2](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/providers/{providerCode}/methods/{method}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/providers/{providerCode}/methods/{method}` | 11 | [11.2](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}` | 11 | [11.2](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/psp-connections` | 11 | [11.5](11-payment-capability-config.activities.md) | composed |
| POST | `/api/v1/payments/psp-connections` | 11 | [11.6](11-payment-capability-config.activities.md) | diagram |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}` | 11 | [11.6](11-payment-capability-config.activities.md) | diagram |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests` | 11 | [11.8](11-payment-capability-config.activities.md) | composed |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests/{approvalId:guid}/test` | 11 | [11.7](11-payment-capability-config.activities.md) | diagram |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}` | 11 | [11.3](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}` | 11 | [11.3](11-payment-capability-config.activities.md) | composed |
| POST | `/api/v1/payments/psp-connections/{connectionId:guid}/test` | 11 | [11.7](11-payment-capability-config.activities.md) | diagram |
| GET | `/api/v1/payments/routing-rulesets` | 11 | [11.5](11-payment-capability-config.activities.md) | composed |
| POST | `/api/v1/payments/routing-rulesets` | 11 | [11.9](11-payment-capability-config.activities.md) | diagram |
| DELETE | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | 11 | [11.9](11-payment-capability-config.activities.md) | diagram |
| GET | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | 11 | [11.1](11-payment-capability-config.activities.md) | composed |
| PUT | `/api/v1/payments/routing-rulesets/{rulesetId:guid}` | 11 | [11.9](11-payment-capability-config.activities.md) | diagram |
| POST | `/api/v1/payments/routing-rulesets/{rulesetId:guid}/activation-requests` | 11 | [11.8](11-payment-capability-config.activities.md) | composed |
| GET | `/api/v1/api-clients` | 12 | [12.1](12-governance-audit-api-clients.activities.md) | composed |
| POST | `/api/v1/api-clients` | 12 | [12.4](12-governance-audit-api-clients.activities.md) | diagram |
| GET | `/api/v1/api-clients/{clientId:guid}` | 12 | [12.1](12-governance-audit-api-clients.activities.md) | composed |
| PUT | `/api/v1/api-clients/{clientId:guid}` | 12 | [12.5](12-governance-audit-api-clients.activities.md) | composed |
| POST | `/api/v1/api-clients/{clientId:guid}/revoke` | 12 | [12.5](12-governance-audit-api-clients.activities.md) | composed |
| POST | `/api/v1/api-clients/{clientId:guid}/secret-rotation-requests` | 12 | [12.6](12-governance-audit-api-clients.activities.md) | diagram |
| POST | `/api/v1/api-clients/secrets/{ticketId}/reveal` | 12 | [12.7](12-governance-audit-api-clients.activities.md) | diagram |
| GET | `/api/v1/approvals` | 12 | [12.1](12-governance-audit-api-clients.activities.md) | composed |
| GET | `/api/v1/approvals/{approvalId:guid}` | 12 | [12.1](12-governance-audit-api-clients.activities.md) | composed |
| POST | `/api/v1/approvals/{approvalId:guid}/approve` | 12 | [12.2](12-governance-audit-api-clients.activities.md) | composed |
| POST | `/api/v1/approvals/{approvalId:guid}/reject` | 12 | [12.2](12-governance-audit-api-clients.activities.md) | composed |
| GET | `/api/v1/audits` | 12 | [12.3](12-governance-audit-api-clients.activities.md) | composed |
| GET | `/api/v1/audits/{auditId:guid}` | 12 | [12.3](12-governance-audit-api-clients.activities.md) | composed |
| GET | `/api/v1/audit-logs` | 13 | [13.2](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | 13 | [13.7](13-notifications-outbound-webhooks.activities.md) | composed |
| PUT | `/api/v1/merchants/{merchantId:guid}/event-endpoint` | 13 | [13.7](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}` | 13 | [13.2](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notification-deliveries/{deliveryId:guid}/attempts` | 13 | [13.2](13-notifications-outbound-webhooks.activities.md) | composed |
| POST | `/api/v1/notification-deliveries/{deliveryId:guid}/retries` | 13 | [13.8](13-notifications-outbound-webhooks.activities.md) | diagram |
| GET | `/api/v1/notifications` | 13 | [13.2](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notifications/{notificationId:guid}` | 13 | [13.2](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notifications/deliveries` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notifications/deliveries/{deliveryId:guid}` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notifications/rules` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| POST | `/api/v1/notifications/rules` | 13 | [13.3](13-notifications-outbound-webhooks.activities.md) | composed |
| DELETE | `/api/v1/notifications/rules/{ruleId:guid}` | 13 | [13.3](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/notifications/rules/{ruleId:guid}` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| PUT | `/api/v1/notifications/rules/{ruleId:guid}` | 13 | [13.3](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/webhooks/deliveries` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/webhooks/deliveries/{deliveryId:guid}` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| POST | `/api/v1/webhooks/deliveries/{deliveryId:guid}/replay` | 13 | [13.6](13-notifications-outbound-webhooks.activities.md) | diagram |
| GET | `/api/v1/webhooks/endpoints` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| POST | `/api/v1/webhooks/endpoints` | 13 | [13.4](13-notifications-outbound-webhooks.activities.md) | diagram |
| DELETE | `/api/v1/webhooks/endpoints/{endpointId:guid}` | 13 | [13.5](13-notifications-outbound-webhooks.activities.md) | composed |
| GET | `/api/v1/webhooks/endpoints/{endpointId:guid}` | 13 | [13.1](13-notifications-outbound-webhooks.activities.md) | composed |
| PUT | `/api/v1/webhooks/endpoints/{endpointId:guid}` | 13 | [13.5](13-notifications-outbound-webhooks.activities.md) | composed |
| POST | `/api/v1/webhooks/notifications/{providerCode}` | 13 | [13.9](13-notifications-outbound-webhooks.activities.md) | diagram |
| GET | `/api/v1/payments/transactions` | 14 | [14.2](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/api/v1/payments/transactions/{paymentSessionId:guid}` | 14 | [14.2](14-inbound-webhooks-reporting-infra.activities.md) | composed |
| GET | `/api/v1/payments/transactions/export` | 14 | [14.4](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/api/v1/reports/dashboard` | 14 | [14.3](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/api/v1/reports/operations` | 14 | [14.3](14-inbound-webhooks-reporting-infra.activities.md) | composed |
| GET | `/api/v1/reports/operations/export` | 14 | [14.4](14-inbound-webhooks-reporting-infra.activities.md) | composed |
| GET | `/api/v1/reports/reconciliation` | 14 | [14.5](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/api/v1/webhooks/inbound-events` | 14 | [14.1](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/api/v1/webhooks/inbound-events/{eventId:guid}` | 14 | [14.1](14-inbound-webhooks-reporting-infra.activities.md) | composed |
| GET | `/health/live` | 14 | [14.6](14-inbound-webhooks-reporting-infra.activities.md) | composed |
| GET | `/health/ready` | 14 | [14.6](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/openapi/{documentName}.json` | 14 | [14.7](14-inbound-webhooks-reporting-infra.activities.md) | diagram |
| GET | `/scalar/{documentName?}` | 14 | [14.7](14-inbound-webhooks-reporting-infra.activities.md) | composed |

---

## Deviations จากเอกสาร inventory

รวม deviation จริงที่พบระหว่างตรวจ source กับเอกสาร inventory ของทุก theme (theme ที่ไม่มีชื่ออยู่ในตารางนี้ = ไม่พบ deviation ระหว่างเอกสารกับ source)

| Theme | fullPath | เอกสารบอก | source บอก | อ้างอิง |
| --- | --- | --- | --- | --- |
| 04 · Carts, products และ order lifecycle | `/api/v1/orders/{orderId:guid}/payment-links` | policy dual-console (คาดว่ารองรับทั้ง Admin Console และ Merchant Console เหมือน endpoint พี่น้องในกลุ่ม order) | ไม่มี branch IsAdminCommerceRequest เลยในซอร์ส และไม่เรียก actorScope.Begin — Admin Console เป็น platform Bearer ที่ไม่มี claim merchant_id เมื่อเรียก endpoint นี้จะได้ actor.MerchantId throw InvalidOperationException แล้วตอบ 409 ผ่าน exception handler กลาง ไม่ใช่ผลลัพธ์ปกติของ dual-console | src/Api/Api/Program.cs:1049-1074, src/Api/Api/HttpActorContext.cs:56-58, src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:89-90 |
| 09 · Merchant provisioning, merchant users และ merchant OIDC | `/api/v1/merchants (POST)` | policy admin (Bearer, ไม่มี CSRF) | RequirePlatformUserTier(Tier.Super) ที่ boundary + re-verify ใน txn ผ่าน ProvisioningCoordinator.VerifyCallerIsActiveSuper (acct.Accounts + access.PlatformAccess active) | src/Api/Api/Program.cs:2370-2411; ProvisioningCoordinator.cs:223-249 |
| 11 · Payment capability configuration | `/api/v1/payments/methods, /api/v1/payments/methods/{method}/options` | § 11.4 diagram กลาง + ตาราง flow ประกอบ (ก่อนแก้) วาด lock timeout ของ resolver เป็น 409 code payment_authorization_busy เหมือนกันทั้ง 5 endpoint | 2 endpoint นี้ (policy merchant-user, map ผ่าน PaymentCapabilityEndpoints) ไม่ได้อยู่ใต้ routes ที่ผูก AddEndpointFilter(HandleKnownErrors) ของ AdminControlEndpoints (คนละ call ใน Program.cs) — HandleKnownErrors เป็นจุดเดียวที่แปลง PaymentAuthorizationBusyException (bare Exception) เป็น 409; เมื่อไม่ผ่าน filter นี้ exception ตกไปที่ ProblemDetailsExceptionHandler ซึ่งไม่มี case ให้ จึงกลายเป็น 500 ProblemDetails ทั่วไปไม่มี code — แก้ไดอะแกรมทั้ง 2 ไฟล์แล้วให้ตรงกับพฤติกรรมจริง | src/Api/Api/Program.cs:770,780; src/Api/Api/ControlPlane/AdminControlEndpoints.cs:18,1042-1045; src/Api/Api/Payments/PaymentCapabilityEndpoints.cs:10-12,29,52; src/Application/Modules/Payments.Application/AdminControlPlane/AdminPaymentsControl.cs:18; src/Api/BuildingBlocks.Web/ProblemDetailsExceptionHandler.cs:48-56,71-100; src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/Capabilities/EffectivePaymentCapabilityResolver.cs:294-306; src/Infrastructure/Persistence/Persistence.ControlPlane/Payments/PaymentAuthorizationSqlLockManager.cs:12-13,38-73 |

---

## Notes

### นอก frame ของ README นี้

- README เป็น index + สรุปเท่านั้น รายละเอียด mermaid, ตาราง flow ประกอบ, Deviations และ Notes เต็มรูปของแต่ละ flow อยู่ในไฟล์ theme (`NN-slug.activities.md` / `.sequences.md`) เอง ไม่ duplicate ซ้ำที่นี่
- ลิงก์ในตารางทุกตัวชี้ไปที่ไฟล์ (relative path) ไม่ชี้ anchor ของหัวข้อ § เฉพาะ เพราะ GitHub slug ของหัวข้อภาษาไทยไม่เสถียรพอจะการันตีว่าลิงก์ตรง — เปิดไฟล์แล้วค้นหา `## <ตัวเลข §>` เอง

### Tool defect: check-mermaid.mjs กับทุก flowchart (must, ทุก theme, ยังเปิดอยู่ — นอก write scope ของชุดนี้)

ทุกไฟล์ `*.activities.md` (00 ถึง 14, ชุดเดิมตอนสร้าง 2026-09-14 มี 123 flowchart block; หลัง retire theme 08 + § 1.6 เมื่อ 2026-09-15 เหลือ 114 block) FAIL การรัน check-mermaid.mjs ด้วย error เดียวกันทุก block: `purify.addHook is not a function` — ส่วน `*.sequences.md` ทุกไฟล์ผ่าน OK ครบ ยืนยันซ้ำแล้วว่าเป็น environment defect ของ mermaid ที่ `mmdc` bundle มา ตอน parse `flowchart TD` เท่านั้น (reproduce ได้ด้วย flowchart 2-3 บรรทัดที่ syntax ถูกต้อง 100% แล้วยัง FAIL ข้อความเดียวกัน) ไม่ใช่ syntax ผิดของเนื้อหา diagram ไฟล์ไหนเลย

**Root cause ที่ยืนยันแล้ว** (ไม่ใช่ปัญหา version mismatch ระหว่าง 2 package): `mermaid` (v11.14.0 ที่ `@mermaid-js/mermaid-cli` bundle มา) ฝัง DOMPurify ทั้งก้อนไว้ในตัวเอง ไม่ได้ resolve จาก npm package `dompurify` แยก — โค้ดที่ฝังมา (`createDOMPurify()`) ถูกเรียกใน lazy chunk ที่โหลดเฉพาะตอน parse `flowchart` โดยไม่มี `window`/`document` (check-mermaid.mjs รันใน plain Node) จึงเข้า branch "ไม่รองรับ" แล้ว return object ที่ยังไม่ถูกผูก `.addHook` เข้าไป (การผูก `.addHook` อยู่คนละจุดกับจุด return) — โค้ด parser ของ flowchart เรียก `purify.addHook(...)` ทันทีตอน parse (เพื่อ sanitize node label ที่มี `<br/>`/HTML) จึง throw; parser ของ `sequenceDiagram` ไม่เดินโค้ด path นี้ตอน parse จึงไม่โดน ข้อสรุป: pin เวอร์ชัน `mermaid`/`dompurify` ให้ตรงกัน **แก้ปัญหานี้ไม่ได้** เพราะไม่มี `dompurify` เป็น dependency แยกให้ pin ต้องฉีด DOM ก่อนเรียก parse แทน

**Evidence เพิ่มเติมที่ทำวันนี้** (ยืนยัน root cause ด้วยการรันจริง 2 วิธี นอกเหนือจาก manual checklist เดิม):

- ฉีด `window`/`document` จาก `jsdom` (`new JSDOM(...)`, ตั้ง `global.window`/`global.document` ก่อน `import` mermaid) แล้วเรียก `mermaid.parse()` ตัวเดียวกับที่ check-mermaid.mjs ใช้ — ได้ `OK` จริงครบทุก block ในทุกไฟล์ `*.activities.md` ทั้ง 15 ไฟล์ (00–14) ไม่มี FAIL แม้แต่ block เดียว (ชุดเดิม 2026-09-14 = **123/123 block**; หลัง retire theme 08 + § 1.6 วัด 2026-09-15 = **114/114 block**)
- Render จริงผ่าน `mmdc` (headless Chrome ของจริง มี `window`/`document` เต็มรูป) ด้วย markdown-mode (`mmdc -p pptr.json -i <file>.activities.md -o <out>.md`) กับ **ทั้ง 15 ไฟล์** `*.activities.md` — ได้ SVG ออกมาครบ **114/114 block** (2026-09-15) ไม่มี error แม้แต่ไฟล์เดียว (จำนวน SVG ต่อไฟล์ = จำนวน ```mermaid block ต่อไฟล์ทุกไฟล์)

ทั้งสองวิธียืนยันตรงกัน: เนื้อหา diagram ทุกไฟล์ syntax ถูกต้อง 100% ปัญหาอยู่ที่ environment ของ check-mermaid.mjs เพียงอย่างเดียว

**Patch ที่จะปิด finding นี้ได้จริง** (แก้ที่ `~/.claude/skills/readable-markdown/check-mermaid.mjs` — อยู่นอก write scope ของชุดไฟล์นี้ ต้องให้ผู้ดูแล skill เป็นคนแก้): เพิ่ม jsdom shim ก่อนบรรทัด `import(...)` ของ mermaid

```js
import { JSDOM } from 'jsdom';
const dom = new JSDOM('<!doctype html><html><body></body></html>', { url: 'http://localhost/' });
global.window = dom.window;
global.document = dom.window.document;
```

ข้อควรระวังตอน apply: ต้อง `npm install jsdom` ในไดเรกทอรีที่ check-mermaid.mjs อยู่ (หรือ global) ก่อน — ตรวจแล้ววันนี้ว่า `jsdom` **ไม่มี** อยู่ใน global node_modules ปัจจุบัน (ทั้งที่ node global lib และใต้ `@mermaid-js/mermaid-cli`) มีแต่ใน scratchpad ของ session ที่ทดสอบเท่านั้น ถ้าไม่อยากเพิ่ม dependency ใหม่ ใช้ทางเลือกที่ไม่ต้องติดตั้งอะไรเพิ่มแทนได้: เปลี่ยนไปใช้ `mmdc` markdown-mode ตรง ๆ (คำสั่งเดียวกับ fallback ที่ skill `diagrams` เอกสารไว้อยู่แล้ว) เป็น gate แทน `check-mermaid.mjs` สำหรับไฟล์ `.activities.md` — วิธีนี้ verify ผ่านจริงแล้วครบทั้ง 15 ไฟล์ (114/114 block) ด้านบน

**สถานะ**: finding นี้ยังเป็น must ที่เปิดอยู่ — ปิดได้เมื่อ (ก) มีคน apply patch ข้างต้น (หรือสลับไปใช้ `mmdc` markdown-mode) แล้วรัน checker ซ้ำได้ OK ครบทุกไฟล์จริง หรือ (ข) orchestrator ออก waiver เป็นลายลักษณ์อักษรว่ายอมรับหลักฐาน jsdom-parse 114/114 + mmdc-render 114/114 (2026-09-15) ข้างต้นแทน automated gate ของ contract ข้อ 1 ชุดไฟล์นี้เอง (README + theme files) ไม่มีสิทธิ์แก้ไฟล์นอก output dir จึงทำได้แค่เอกสารหลักฐานและข้อเสนอ patch ไว้ตรงนี้

### Content-level residual must-fix ที่ยังค้าง (ไม่รวม tool defect ด้านบน)

| Theme | จำนวน | สรุป |
| --- | --- | --- |
| 01 | 0 | - |
| 02 | 6 | § 2.3 bump-version guard ของ PATCH accounts และ PUT merchant-access/platform-access ไม่ถูกต้องครบทุกกรณี, § 2.6 POST .../keys ไม่ bump AuthorizationVersion แต่ diagram ผูกรวมกับอีก 3 endpoint |
| 03 | 0 | - |
| 04 | 2 | § 4.5 ไม่มี branch cart.OriginatorId mismatch ฝั่ง Merchant, § 4.4 รายการ error code 409 จาก TrustedOrderPricingSource ไม่ครบ (source_quantity_invalid, source_owner_mismatch) |
| 05 | 5 | § 5.2 verify ไม่ได้ระบุ 3 ผลลัพธ์จริงของ VerifyAsync, § 5.9/5.4 label R500 ไม่แยกกรณี mark Failed จริง, § 5.10 เหตุผล 401 Rejected ไม่ครบ (ขาด binding_mismatch) |
| 06 | 1 | § 6.2 ไม่มี branch validation ของ NotificationIntent (email/phone/recipient required) |
| 07 | 3 | § 7.5 ลำดับ node ENVK ไม่ตรง source, ไม่มี 404 branch ที่ CRED/ENVK และไม่มี 400 invalid_idempotency_key, sequences Phase B ไม่มี EnsureAccess/idempotency ของ 3 kind |
| 08 | 0 | - |
| 09 | 2 | § 9.4 ไม่วาด async/outbox (RegistrationConsumer, KycPhotoLifecycleConsumer), § 9.6 TTL label สื่อผิดว่าเป็น field ใน request body |
| 10 | 3 | § 10.2 ไม่มี branch validation เพิ่มเติม (payment method channel, name, metadata), § 10.8/10.9 ไม่มี branch validate Type และใช้ชื่อ field apiClientId ผิดจาก linkedApiClientId |
| 11 | 0 | - |
| 12 | 0 | - |
| 13 | 6 | § 13.3/13.4 label 403 AccessDeniedException ไม่มี code ผิด (จริงมี code permission_denied เสมอ), § 13.3/13.5 label 409 ConcurrencyConflict ไม่มี code ผิด (จริงมี code state_conflict เสมอ), § 13.7 ลำดับ IDEM ก่อน LOOKUP ผิดจาก source |
| 14 | 0 | - |

รายละเอียดเต็ม (evidence, path:line, fix ที่แนะนำ) อยู่ใน Notes/ตาราง flow ประกอบของแต่ละไฟล์ theme นั้น ๆ

### วิธี verify

รัน check-mermaid.mjs กับทุกไฟล์ (`00`–`14`, ทั้ง `.activities.md` และ `.sequences.md`) ทีละไฟล์:

```bash
node /Users/king_developer/.claude/skills/readable-markdown/check-mermaid.mjs <absolute-path-to-file>.md
```

ต้องได้ `OK` ทุก block ในไฟล์ `.sequences.md` ทั้งหมด (ผ่านจริงในสภาพแวดล้อมปัจจุบัน); ไฟล์ `.activities.md` ยัง FAIL ด้วยคำสั่งนี้ตรง ๆ เพราะ tool defect ด้านบน (แก้ที่ dependency ไม่ได้ ต้องฉีด DOM — ดูรายละเอียด root cause และ patch ด้านบน) ระหว่างที่ patch ยังไม่ถูก apply ให้ verify แทนด้วยวิธีใดวิธีหนึ่ง:

- ฉีด jsdom ก่อน import mermaid แล้วเรียก `mermaid.parse()` ตรง ๆ (โค้ด shim เดียวกับ patch ด้านบน) — วิธีนี้ยืนยันแล้วว่าได้ `OK` ครบทุก block ทั้ง 15 ไฟล์
- หรือ `mmdc -p pptr.json -i <file>.activities.md -o <out>.md` (markdown-mode, ไม่ต้องติดตั้งอะไรเพิ่ม) ตาม fallback ของ skill `diagrams` — render ผ่านจริงได้ = syntax ถูก

หลัง render ผ่าน ให้รัน grep verify ข้อ 4 (stale-token sweep) — ทุก pattern ต้องว่าง ยกเว้น retire-note ที่ตั้งใจ:

```bash
cd outputs/diagrams/2026-09-14_api-endpoints_v1
grep -rnE 'adm_csrf|__Host-adm_session|BffSession|__Host-pol_session|IdentityAccessBff|Bearer/BFF' *.md   # ต้องว่าง
grep -rnE 'RequireCsrf' *.md   # CSRF ที่ยอมรับได้ = RequireUserCsrf (merchant-user mch_csrf) เท่านั้น;
  # RequireAudienceCsrf (dual-console) และ retire-note ที่เขียนว่า RequireCsrf() ถูก retire = ยอมรับได้;
  # bare RequireCsrf() ที่ใช้เป็น live gate ของ route admin = stale ต้องแก้เป็น "policy admin (Bearer) ไม่มี CSRF ดู § 0.1"
```

ยืนยันกับ source: admin `.RequireCsrf()` และ `Admins/CsrfFilter.cs` ถูกลบจริง (retire) — survivors ใน source = `RequireUserCsrf`/`RequireAudienceCsrf` (`Program.cs`, `Iam/CsrfParity.cs`), `mch_csrf` (`Merchants/UserCsrfFilter.cs`)

**Render**: GitHub / Obsidian / VS Code Mermaid
