# Design: Admin Console Real API Backend Contract

> Status: unknown

`pol-core` extends current domain owners and area-first Minimal API routes. This mirror locks backend
ownership; exact request/response/error inventory remains aligned with
`pol-admin/.claude/specs/real-api-integration/design.md` at baseline `pol-core@83c86cb` and
`pol-merchant@8191b44`.

## Architecture Overview

| Capability | Owner | Minimum change |
| --- | --- | --- |
| Admin and composite authentication | `Hosts.Api`, `Admins`, `Iam` | Reuse Admin session; add deterministic `dual-console` only where one existing path serves both audiences. |
| Admin identity and organization | `Admins`, `Iam`, `Offices`, `Divisions`, `Positions`, `Levels` | Extend existing handlers with paging, ETag, idempotency where required, and stable OpenAPI. |
| Tenant and merchant users | `Merchants`, `Persistence.ControlPlane`, `Persistence.MerchantUsers` | Extend existing merchant/user handlers; reuse `ProvisioningCoordinator` and `MerchantUserInvitation`. |
| Commerce | `Products`, `Carts`, `Orders`, `Payments` | Add Admin actor branches with explicit merchant/originator; preserve Merchant contracts and operation IDs. |
| PSP and routing | `Payments`, `Persistence.MerchantRuntime` | Owner-local staged secrets, routing drafts, activation, capability, and durable operation state. |
| Governance and delivery | `Governance`, `Notifications`, `Persistence.ControlPlane` | Owner outbox protocol, approvals, immutable audit, API clients, webhooks, notifications, local secret versions. |
| Read models | query-only `Reporting`; reconciliation stays `Orders` | Dashboard, transaction projection, reports, and bounded exports; no second money ledger. |

No new generic transaction coordinator. Existing `ProvisioningCoordinator` stays sole allowlisted raw
cross-context transaction and serves `ProvisionMerchant` only. Every other mutation commits target
state plus owner outbox in one owner Unit of Work. Governance consumes requests, persists decisions,
and emits decision events; target owner executes idempotently and publishes outcome. Governance never
writes target DbContext.

## Admin Scope and Composite Audience

At request boundary, resolve active Admin status, tier, permissions, merchant assignments, and current
`AuthorizationVersion`. `Super` can read aggregates without merchant; every merchant mutation still
requires explicit `merchantId`. `Scoped` reads and writes assigned merchants only. Out-of-scope detail
returns tenant-safe `404`; mutation returns coded `403 merchant_scope_forbidden`.

`dual-console` applies only to existing Commerce routes plus `ListMerchantUsers` and
`GetMerchantUser`:

1. Admin cookie present selects `AdminSession`; otherwise select `MerchantUserSession`.
2. Both cookies select Admin deterministically.
3. Invalid or expired Admin cookie returns `401`; never fall back to Merchant.
4. `RequireAudiencePermission(adminKey, merchantKey)` checks selected scope only.
5. `RequireAudienceCsrf()` validates selected audience for unsafe requests.
6. OpenAPI publishes `AdminSession OR MerchantUserSession`, not AND.

Task 1 installs these shared primitives and isolated host tests only. Existing routes remain pinned to
`merchant-user` until Task 5 or Task 6 adds the matching Admin owner branch; policy adoption and branch
dispatch land atomically. Otherwise current handlers would admit Admin authentication while still reading
Merchant-only `IActorContext` or `IUserScope.Current`.

Permission additions are platform-scoped `txn.manage`, `merchants.users.manage`,
`merchants.roles.view`, and `merchants.roles.manage`. Last three join existing platform group
`merchants.users`. Catalog constants, seed grants, boot parity, endpoint metadata, and tests land
together. Merchant-side keys remain canonical `payment.*`, `users.view`, and existing Merchant role
keys.

## Persistence and Recovery

| Store | Purpose |
| --- | --- |
| `admin.OperationRecords` | ControlPlane credential/governed mutations; actor + operation + idempotency key, canonical request hash, bounded result. |
| `txn.AdminOperationRecords` | MerchantRuntime Admin commerce/PSP/routing mutations. |
| `merch.AdminUserOperationRecords` | Merchant-user Admin mutations in MerchantUser owner context. |
| `PaymentOperation` | PSP financial unknown-outcome state machine; no generic record replaces it. |
| `admin.DeliverySecretVersions` | ControlPlane-owned Webhook/Notification delivery secrets, encrypted with shared `IKekProvider`. |

Owner target state, operation record, and outbox commit together. Same idempotency key and normalized
intent returns recorded status/body; changed intent returns `409 idempotency_key_reused`. Sensitive
values are replaced in the request hash by staged version ID. ControlPlane delivery secrets never
reference MerchantRuntime Vault. PSP credentials continue using MerchantRuntime Vault.

Secret changes use `Staged → Active → Retired/Discarded`. Activation is version checked; replay after
commit-unknown resolves through owner operation record. API-client secret uses keyed HMAC and a
single-use, no-store reveal ticket. Reads, errors, audit, and logs never contain raw secret.

## Owner Contract Inventory

Existing path/operation pairs are extended in place. New paths follow area owner.

| Owner slice | Operations | Audience and permissions | Guards |
| --- | --- | --- | --- |
| Admin identity | Existing `/api/v1/admins/**`, `/api/v1/admins/roles/**`, `/api/v1/admins/permissions` | Admin-only, existing `user.*`/role permissions | CSRF, ETag, operation record for credential/session mutation |
| Organization | Existing `/api/v1/offices`, `/divisions`, `/positions`, `/levels` | Admin-only, existing owner permissions | server paging, ETag, stable status `1/2` |
| Tenants | Existing provision/detail plus list/update/suspend/reactivate under `/api/v1/merchants` | `merchant.view` / `merchant.manage` | explicit scope, ETag, idempotency |
| Originators | `/api/v1/originators` CRUD/state for branch, agent, broker, staff, app | `merchant.view` / `merchant.manage` | explicit scope, reference-safe deactivate |
| Merchant-user reads | Existing `ListMerchantUsers`, `GetMerchantUser` | Admin `merchants.users.view`; Merchant `users.view` | `dual-console`, preserve path/ID/handler |
| Merchant-user writes | Admin invitation/update plus existing approve/reject | `merchants.users.manage` and existing approval keys | reuse canonical invitation; no raw token; ETag/idempotency |
| Merchant roles | `/api/v1/merchants/{merchantId}/roles/**` and user-role assignment | `merchants.roles.view` / `merchants.roles.manage` | Admin-only branch, canonical IAM Merchant catalog |
| Products | `GET /api/v1/products/documents` | Admin product read permission | explicit merchant/originator, decimal strings, paged filters |
| Carts and Orders | Existing cart/order paths and operation IDs | Admin `txn.manage`; Merchant existing `payment.*` | `dual-console`, audience CSRF, explicit Admin scope, ETag/idempotency |
| Payment sessions | Existing payment-session paths and operation IDs | Admin `txn.manage`; Merchant existing `payment.*` | request `oneOf`: Admin sends merchant, never PSP; Merchant sends PSP, never merchant |
| PSP/routing | `/api/v1/payments/psp-connections/**`, `/routing-rulesets/**` | `settings.manage` | staged credentials, activation approval, no fallback after PSP call starts |
| Dashboard/transactions/reports | `/api/v1/reports/dashboard`, `/api/v1/payments/transactions/**`, exports, operations reports | `txn.view`/report permissions | authorized read ports, bounded filters/exports, formula escaping |
| Reconciliation | Existing `GetReconciliationReport` and owner export | existing transaction/report read permission | stays owned by `Orders`; no duplicate Reporting operation |
| Governance/audit | `/api/v1/approvals/**`, `/api/v1/audits/**` | `settings.manage`, underlying action permission, `audit.view` | maker cannot decide, version check, append-only hash chain |
| API clients | `/api/v1/api-clients/**` | `apikey.manage` | idempotency, one-time reveal, constant-time verification |
| Outbound delivery | `/api/v1/webhooks/endpoints/**`, delivery query/replay | `settings.manage` | SSRF re-resolution, pinned connect, no redirect, replay eligibility |
| Notifications | `/api/v1/notifications/rules/**`, delivery query | `settings.manage` | create `C I`; update/delete `U I`; local secret versions |

`CreatePaymentSession` keeps existing Merchant success contract. OpenAPI request is audience-aware
`oneOf`: Merchant `{ orderId, method, psp }`; Admin `{ orderId, method, merchantId }`. Backend selects
Admin PSP from active routing. Before active routing exists, validated `Psp:DefaultCode` is used only
when eligible; no fallback starts after a PSP call begins.

Admin-created invitation enters canonical `CreateInvitationHandler`, table
`merch.MerchantUserInvitations`, outbox, and delivery flow. It binds explicit authorized merchant plus
exact invited email. Consume verifies email/tenant, expiry/revocation, and atomically marks one success.
Acceptance URL uses configured `pol-merchant` origin. Admin response returns only invitation ID,
masked email, expiry, and status.

## Errors, Money, and OpenAPI

- RFC 9457 Problem Details carries allowlisted stable code and safe correlation ID.
- Validation `400`, audience/scope `401/403`, tenant-safe `404`, stale/idempotency `409`, expired
  single-use resource `410`, dependency `503`, and sanitized upstream `502` are pinned per route.
- New operational projections serialize `DECIMAL(19,4)` as fixed four-decimal strings.
- Every mutable detail returns `ETag`; mutation uses `If-Match` where applicable.
- Task 3 adds a persisted, monotonic resource `Version` to Admin User, IAM Role, Office, Division,
  Position, and Level. Current source has no resource version for these records;
  `User.AuthorizationVersion` remains the authorization-lease token and MUST NOT be reused as the
  resource ETag because a profile-only edit must not invalidate authenticated sessions.
- Financial, credential, replay, activation, invitation, and Notification secret mutations require
  `Idempotency-Key` and document it in OpenAPI.
- Existing Merchant success bodies and JSON-number Product contract stay unchanged.

## Testing Strategy

- Host tests cover isolated Admin-only, Merchant-only, both-cookie Admin precedence, invalid-Admin no-fallback,
  audience permission/CSRF denial, OpenAPI OR, and permission boot parity.
- Owner tests cover validation, scope, persistence, concurrency, replay, outbox, and stable errors.
- Architecture tests reject new raw cross-context transactions, owner bypasses, duplicate operations,
  secret reads/logs, and fake capability endpoints.
- Integration tests cover migrations, grants, tenant isolation, idempotency, outbox/inbox, and staged
  activation when local test principals exist.
- OpenAPI tests pin paths, operation IDs, audience security, request `oneOf`, paging, ETag,
  idempotency headers, decimal strings, nullability, and Problem Details.

## Requirement Traceability

| Section | REQ |
| --- | --- |
| Real persisted/OpenAPI boundary and preserved owners | REQ-1.1–REQ-1.2, REQ-1.6–REQ-1.8, REQ-1.10 |
| Admin session, scope, composite audience, public-surface split | REQ-2.1–REQ-2.2, REQ-2.7–REQ-2.11, REQ-2.13–REQ-2.16 |
| Reporting aggregates | REQ-3.1–REQ-3.4, REQ-13.1–REQ-13.7 |
| Admin commerce and Orders | REQ-4.1–REQ-4.6, REQ-4.8–REQ-4.10, REQ-4.14–REQ-4.16, REQ-5.1–REQ-5.7, REQ-5.9–REQ-5.14 |
| Transaction projection and financial operation state | REQ-6.1–REQ-6.14 |
| Admin identity and organization | REQ-7.1–REQ-7.11, REQ-9.1–REQ-9.6, REQ-9.8 |
| Merchant users, invitation, and roles | REQ-8.1–REQ-8.14 |
| PSP and routing | REQ-10.1–REQ-10.12 |
| API clients and webhooks | REQ-11.1–REQ-11.12 |
| Governance, audit, and notifications | REQ-12.1–REQ-12.14 |
| Tenant and originator control plane | REQ-14.1–REQ-14.8 |
| Shared money/error/idempotency/concurrency contract | REQ-15.9–REQ-15.15 |
| Backend verification | REQ-16.3, REQ-16.6, REQ-16.8, REQ-16.13 |
