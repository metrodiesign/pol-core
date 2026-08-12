# Requirements: Merchant Frontend Real API Backend Contract

> Status: approved 2026-08-10 (mirror of approved Merchant spec)

เอกสารนี้ mirror เฉพาะเกณฑ์ที่ `pol-core` เป็นเจ้าของจาก
`pol-merchant/.claude/specs/merchant-real-api-integration/requirements.md`. หมายเลข REQ คงเดิมเพื่อให้
frontend, backend, OpenAPI และ tests ใช้ vocabulary เดียวกัน.

## REQ-2: Network topology and same-origin delivery

- 2.2 THE SYSTEM SHALL expose Merchant API calls through explicit same-origin routes without a generic ambiguous rewrite.
- 2.10 WHERE a reverse proxy supplies forwarded host or protocol THE SYSTEM SHALL trust only configured narrow proxy IPs or CIDRs.
- 2.11 IF a configured trusted network is wildcard IPv4 or IPv6 THEN THE SYSTEM SHALL fail startup.

## REQ-3: Merchant session lifecycle

- 3.1 WHEN Google OIDC login succeeds THE SYSTEM SHALL create an opaque Merchant session cookie inaccessible to frontend JavaScript.
- 3.3 IF a known account is pending, rejected, suspended, or unbound THEN THE SYSTEM SHALL return its allowlisted lifecycle code without exposing identity details.
- 3.7 WHEN a Merchant mutation is sent THE SYSTEM SHALL require the backend-issued CSRF value.
- 3.15 THE SYSTEM SHALL build the OIDC callback URI from the trusted browser-facing host and protocol.
- 3.16 IF a forwarded host or protocol comes from an untrusted peer THEN THE SYSTEM SHALL ignore it.

## REQ-4: Registration contract

- 4.3 WHEN registration is submitted THE SYSTEM SHALL accept canonical field `producerCode` and map it to the domain sale code.
- 4.5 WHEN registration is submitted THE SYSTEM SHALL require one JPEG, PNG, or WebP photo no larger than 2 MiB.
- 4.8 IF a registration ticket is invalid, expired, or reused THEN THE SYSTEM SHALL return stable code `registration-link-invalid`.
- 4.13 THE SYSTEM SHALL NOT expose registration ticket internals or stored object keys in an error response.

## REQ-5: Product catalogue contract

- 5.1 WHEN products are listed THE SYSTEM SHALL scope the upstream query with the authenticated user's sale code.
- 5.5 THE SYSTEM SHALL return only persisted or upstream product fields without fabricated customer, price, or status data.
- 5.9 WHERE a product is already sold by this platform THE SYSTEM SHALL mark it unavailable for another cart.
- 5.10 WHERE an upstream product has `paymentStatus=PAID` THE SYSTEM SHALL reject adding it to a cart.
- 5.11 THE SYSTEM SHALL expose stable `productCode` and `variantCode` for cart mutations.
- 5.12 IF the authenticated user has no sale code THEN THE SYSTEM SHALL return `403` with code `sale-code-missing`.

## REQ-6: Cart, checkout, and payment link

- 6.1 WHEN a cart is created THE SYSTEM SHALL persist it under the authenticated merchant scope.
- 6.5 WHEN a product is added THE SYSTEM SHALL derive authoritative price and metadata server-side.
- 6.6 WHEN cart quantity changes THE SYSTEM SHALL return the updated persisted cart view.
- 6.7 WHEN a cart item is removed THE SYSTEM SHALL return the updated persisted cart view.
- 6.8 WHEN a cart is cleared THE SYSTEM SHALL return the updated persisted cart view.
- 6.11 WHEN checkout is confirmed THE SYSTEM SHALL create an order from the persisted cart without accepting a client amount.
- 6.16 THE SYSTEM SHALL select the configured default PSP and SHALL reject an unknown configured PSP during startup.
- 6.17 WHEN an order exists THE SYSTEM SHALL provide a real resend-summary operation.
- 6.21 IF a cart mutation fails THEN THE SYSTEM SHALL leave the last backend-confirmed cart state unchanged.
- 6.24 IF summary-link creation fails after order commit THEN THE SYSTEM SHALL preserve the order identifier for recovery.

## REQ-7: Orders

- 7.1 WHEN orders are listed THE SYSTEM SHALL return a server-paginated merchant-scoped result.
- 7.3 WHEN order detail is requested THE SYSTEM SHALL return the persisted order and line snapshot.
- 7.6 WHEN an eligible order is cancelled THE SYSTEM SHALL persist one legal lifecycle transition.
- 7.11 THE SYSTEM SHALL validate only allowlisted order filters and sorts.
- 7.12 IF an order filter is malformed or unsupported THEN THE SYSTEM SHALL return a stable validation error.

## REQ-8: Payment sessions

- 8.1 WHEN payment sessions are listed THE SYSTEM SHALL return a server-paginated merchant-scoped result.
- 8.3 THE SYSTEM SHALL derive payment amount and currency from the persisted order.
- 8.6 WHEN an anonymous payment capability is used THE SYSTEM SHALL not require or consume a Merchant cookie.
- 8.10 WHEN the same payment capability is retried THE SYSTEM SHALL not create a duplicate charge.

## REQ-9: Merchant-user management

- 9.1 THE SYSTEM SHALL scope every Merchant-user read and mutation to the authenticated merchant.
- 9.5 WHEN an invitation is created THE SYSTEM SHALL store only a hash of its single-use token.
- 9.7 WHEN an invitation starts registration THE SYSTEM SHALL bind the verified email and merchant from that invitation.
- 9.10 WHEN invitation registration succeeds THE SYSTEM SHALL consume the invitation atomically with the pending user creation.
- 9.14 IF invitation consume races or replays THEN THE SYSTEM SHALL allow at most one success.
- 9.18 WHEN user lifecycle changes THE SYSTEM SHALL append a redacted management audit event.
- 9.22 THE SYSTEM SHALL prevent suspension or role removal from deleting the last active Merchant manager.

## REQ-10: Merchant RBAC

- 10.1 THE SYSTEM SHALL expose Merchant-scoped role and permission APIs backed by the canonical IAM catalog.
- 10.4 THE SYSTEM SHALL keep Platform permission keys and Merchant permission keys in distinct scopes.
- 10.6 WHEN a role mutation is requested THE SYSTEM SHALL reject permissions from the wrong scope.
- 10.11 THE SYSTEM SHALL enforce the canonical permission at every Merchant endpoint.

## REQ-12: Stable errors and sensitive-data handling

- 12.3 THE SYSTEM SHALL return RFC 9457 Problem Details for API failures.
- 12.4 THE SYSTEM SHALL expose only allowlisted machine codes needed by the frontend.
- 12.5 WHEN a successful operation has no response body THE SYSTEM SHALL return `204` without JSON parsing.
- 12.8 THE SYSTEM SHALL not expose tokens, secret values, raw PII, or internal object keys in public errors.
- 12.10 IF a dependency is unavailable THEN THE SYSTEM SHALL return a retryable `503` without fabricated success.

## REQ-13: Contract and release evidence

- 13.1 THE SYSTEM SHALL publish every changed request, response, error, and security contract in OpenAPI.
- 13.3 THE SYSTEM SHALL prove tenant isolation, permission gates, concurrency, and migration behavior with automated tests.
- 13.9 THE SYSTEM SHALL keep every mirrored REQ covered by design and implementation tasks.
- 13.10 THE SYSTEM SHALL pass build, offline tests, integration tests when credentials are available, secret scan, and spec trace before merge.

## Edge Cases and Decisions

- `pol-merchant` remains canonical for user-facing criteria. This mirror owns backend behavior only.
- Wildcard trusted proxy networks are invalid configuration; there is no compatibility fallback.
- SQL integration evidence is conditional on local principal passwords and never prints those values.
