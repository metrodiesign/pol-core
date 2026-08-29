# Requirements: Admin Console Real API Backend Contract

> Status: unknown

เอกสารนี้ mirror เฉพาะเกณฑ์ที่ `pol-core` เป็นเจ้าของจาก
`pol-admin/.claude/specs/real-api-integration/requirements.md`. หมายเลขเกณฑ์คงเดิมเพื่อให้
frontend, backend, OpenAPI และ tests ใช้ traceability spine เดียวกัน. Baseline คือ
`pol-core@83c86cb` และ `pol-merchant@8191b44`.

## REQ-1: Real Data Boundary

- 1.1 THE SYSTEM SHALL expose persisted product entities required by product routes outside `/minimals/*` through `pol-core` APIs.
- 1.2 THE SYSTEM SHALL persist every product mutation required by product routes outside `/minimals/*` through a `pol-core` API endpoint.
- 1.6 WHERE a required backend capability is absent THE SYSTEM SHALL add that capability before frontend wiring.
- 1.7 WHEN a backend capability is added or changed THE SYSTEM SHALL expose its request, response, error, and security contract in OpenAPI.
- 1.8 THE SYSTEM SHALL preserve behavior of existing public API operations unless an approved contract change explicitly supersedes it.
- 1.10 WHERE a backend operation already owns the same HTTP method and path THE SYSTEM SHALL extend that operation's authenticated actor branch and SHALL preserve its operation ID and handler ownership unless an approved contract change explicitly replaces it.

## REQ-2: Internal Admin Authentication and Authorization

- 2.1 THE SYSTEM SHALL protect every Admin product operation with `AdminSession`.
- 2.2 THE SYSTEM SHALL NOT require `MerchantUserSession` to operate an internal Admin branch.
- 2.7 WHEN a state-changing Admin request is sent THE SYSTEM SHALL require the backend-issued CSRF value using the documented header.
- 2.8 THE SYSTEM SHALL keep access tokens and refresh tokens inaccessible to frontend JavaScript.
- 2.9 THE SYSTEM SHALL enforce Admin permission and merchant scope at the backend for every protected operation.
- 2.10 WHEN logout succeeds THE SYSTEM SHALL invalidate the active Admin session.
- 2.11 WHEN logout-all succeeds THE SYSTEM SHALL invalidate every Admin session owned by the current identity.
- 2.13 WHERE the authenticated Admin has `Super` tier THE SYSTEM SHALL allow aggregate reads without a selected merchant and SHALL require an explicit merchant for every merchant-scoped mutation.
- 2.14 WHERE the authenticated Admin has `Scoped` tier THE SYSTEM SHALL restrict reads and mutations to assigned merchants and SHALL reject any out-of-scope merchant identifier.
- 2.15 THE SYSTEM SHALL NOT add merchant-user login, OIDC callback, registration, invitation acceptance, or public payment routes to the Admin surface.
- 2.16 WHEN an Admin-created merchant-user invitation is delivered THE SYSTEM SHALL build its acceptance URL from the configured public `pol-merchant` origin.

## REQ-3: Dashboard

- 3.1 THE SYSTEM SHALL provide dashboard summary values through a backend aggregate API.
- 3.2 THE SYSTEM SHALL aggregate payment volume, transaction count, success count, failure count, and pending count for the selected period.
- 3.3 THE SYSTEM SHALL provide real breakdowns by PSP, payment channel, and originator.
- 3.4 WHEN dashboard period changes THE SYSTEM SHALL return aggregates for that bounded period.

## REQ-4: Policy, Cart, Checkout, and Payment Link

- 4.1 THE SYSTEM SHALL provide a server-paginated Admin product-document list using active filters.
- 4.2 THE SYSTEM SHALL support documented search, insured name, document reference, insurance type, product group, payment status, and coverage-date filters.
- 4.3 THE SYSTEM SHALL derive sale-code identity from persisted originator scope and SHALL validate the explicit merchant against the Admin scope.
- 4.4 WHEN an Admin starts a cart THE SYSTEM SHALL persist it under the authorized explicit merchant and originator.
- 4.5 WHEN an Admin adds, updates, removes, or clears an item THE SYSTEM SHALL mutate the persisted cart.
- 4.6 WHEN checkout is revisited THE SYSTEM SHALL resolve items from persisted cart or order state.
- 4.8 WHEN checkout is confirmed THE SYSTEM SHALL create an order from the persisted cart.
- 4.9 WHEN an order requires payment THE SYSTEM SHALL create a real payment session for that order.
- 4.10 WHEN a redirect or payment link is issued THE SYSTEM SHALL return the backend-hosted URL.
- 4.14 IF a product is no longer payable at confirmation time THEN THE SYSTEM SHALL reject that item with its current state.
- 4.15 WHEN an Admin creates or mutates a cart, order, or payment session THE SYSTEM SHALL require an explicit merchant identifier validated by the backend.
- 4.16 THE SYSTEM SHALL expose Admin-scoped commerce branches without granting `MerchantUserSession` to an internal Admin.

## REQ-5: Orders

- 5.1 THE SYSTEM SHALL return server-paginated orders within authorized Admin scope.
- 5.2 THE SYSTEM SHALL apply documented order filters at the backend.
- 5.3 THE SYSTEM SHALL return order detail and audit timeline by stable identifier.
- 5.4 THE SYSTEM SHALL return persisted amount, currency, status, payment-session relation, line items, customer reference, and timestamps.
- 5.5 WHEN an eligible order is cancelled THE SYSTEM SHALL invoke the real order operation.
- 5.6 WHEN an eligible order summary is resent THE SYSTEM SHALL invoke the real resend operation.
- 5.7 WHEN a backend-supported order or payment action is approved and invoked THE SYSTEM SHALL execute the real owner operation.
- 5.9 WHEN an order action succeeds THE SYSTEM SHALL expose resulting persisted state.
- 5.10 IF an order action conflicts with current state THEN THE SYSTEM SHALL return a stable conflict and current version.
- 5.11 IF an order identifier does not exist within authorized scope THEN THE SYSTEM SHALL return tenant-safe `404`.
- 5.12 WHEN orders are exported THE SYSTEM SHALL export real rows matching the active backend query.
- 5.13 WHERE an action is absent from the backend capability matrix THE SYSTEM SHALL report it unavailable and SHALL NOT simulate success.
- 5.14 THE SYSTEM SHALL dispatch order cancel/resend to `Orders` and payment lifecycle operations to `Payments`.

## REQ-6: Transactions and Payment Sessions

- 6.1 THE SYSTEM SHALL return a server-paginated Admin transaction projection within authorized scope.
- 6.2 THE SYSTEM SHALL support backend filtering by status, channel, PSP, originator, tenant, date range, and searchable reference.
- 6.3 THE SYSTEM SHALL return the current Order, PaymentSession, and lifecycle-event projection.
- 6.4 THE SYSTEM SHALL return persisted lifecycle events.
- 6.5 THE SYSTEM SHALL NOT synthesize absent order, customer, policy, PSP, or timestamp data.
- 6.6 WHEN a supported payment operation is approved and confirmed THE SYSTEM SHALL invoke the real backend owner.
- 6.7 WHEN a financial mutation succeeds THE SYSTEM SHALL return or expose the resulting persisted state.
- 6.8 WHEN a financial mutation succeeds THE SYSTEM SHALL append an immutable audit record.
- 6.9 THE SYSTEM SHALL make financial mutation retry safe through its durable operation state.
- 6.10 IF a PSP rejects a mutation THE SYSTEM SHALL return a sanitized rejection and current state.
- 6.11 WHEN transactions are exported THE SYSTEM SHALL export rows matching the active backend query.
- 6.12 THE SYSTEM SHALL derive the Admin transaction projection from Order, PaymentSession, and lifecycle events without a second money ledger or Transaction aggregate.
- 6.13 THE SYSTEM SHALL derive available actions from a backend capability matrix for current PSP and state.
- 6.14 WHEN a refund is requested THE SYSTEM SHALL require maker-checker approval and execute only after a different authorized Admin approves it.

## REQ-7: Admin Users and Roles

- 7.1 THE SYSTEM SHALL return real Admin users within caller scope.
- 7.2 THE SYSTEM SHALL return Admin profile, status, tier, assignments, roles, effective permissions, and sessions.
- 7.3 THE SYSTEM SHALL persist authorized Admin creation.
- 7.4 THE SYSTEM SHALL persist authorized Admin profile changes.
- 7.5 THE SYSTEM SHALL persist authorized tier, role, and merchant-assignment changes.
- 7.6 THE SYSTEM SHALL persist authorized Admin suspension and reactivation.
- 7.7 THE SYSTEM SHALL invalidate a selected Admin session when authorized.
- 7.8 THE SYSTEM SHALL return Admin roles and the canonical permission catalog.
- 7.9 THE SYSTEM SHALL persist authorized Admin-role create, update, and delete operations.
- 7.10 IF role deletion conflicts with assignments THEN THE SYSTEM SHALL keep the role and return a stable conflict.
- 7.11 IF an Admin-user mutation violates tier or permission policy THEN THE SYSTEM SHALL persist no change.

## REQ-8: Merchant Users and Roles Managed by Admins

- 8.1 THE SYSTEM SHALL return server-paginated merchant users within authorized Admin scope.
- 8.2 THE SYSTEM SHALL return real profile, registration history, merchant, status, roles, and effective permissions.
- 8.3 THE SYSTEM SHALL execute authorized pending-user approval.
- 8.4 THE SYSTEM SHALL execute authorized pending-user rejection with a reason.
- 8.5 THE SYSTEM SHALL persist authorized merchant-user status, profile, merchant, and role changes through an Admin-scoped branch.
- 8.6 THE SYSTEM SHALL provide merchant-owned roles through an Admin-scoped API.
- 8.7 THE SYSTEM SHALL persist authorized merchant-role create, update, and delete operations through an Admin-scoped API.
- 8.8 THE SYSTEM SHALL NOT require merchant self-service authentication for an `AdminSession` branch.
- 8.9 IF approval state changed before confirmation THEN THE SYSTEM SHALL reject the stale decision.
- 8.10 WHEN an authorized Admin creates a merchant user THE SYSTEM SHALL create a merchant-scoped invitation for OIDC registration.
- 8.11 THE SYSTEM SHALL NOT mint a local password or external identity directly from the Admin operation.
- 8.12 THE SYSTEM SHALL reuse canonical `MerchantUserInvitation` and its persistence store for both inviter audiences.
- 8.13 WHEN invited identity registers THE SYSTEM SHALL enforce identical verified-email, tenant, expiry, revocation, replay, and atomic-consume rules.
- 8.14 THE SYSTEM SHALL NOT return raw invitation tokens from Admin list, detail, or mutation responses.

## REQ-9: Organization Master Data

- 9.1 THE SYSTEM SHALL return server-paginated authorized Office, Division, Position, and Level collections.
- 9.2 THE SYSTEM SHALL return an organization record by stable identifier.
- 9.3 THE SYSTEM SHALL persist authorized organization creation using documented fields.
- 9.4 THE SYSTEM SHALL persist authorized organization updates.
- 9.5 THE SYSTEM SHALL persist inactive status instead of fabricating hard deletion.
- 9.6 THE SYSTEM SHALL preserve status `1` as active and status `2` as inactive.
- 9.8 IF creation conflicts on immutable code THEN THE SYSTEM SHALL return a stable conflict without partial persistence.

## REQ-10: PSP Connections and Routing

- 10.1 THE SYSTEM SHALL return persisted PSP connections and health state.
- 10.2 THE SYSTEM SHALL persist authorized non-secret PSP configuration.
- 10.3 WHEN PSP credentials are submitted THE SYSTEM SHALL encrypt them at the backend boundary.
- 10.4 THE SYSTEM SHALL never return stored PSP secrets in read responses.
- 10.5 WHEN PSP connection test is requested THE SYSTEM SHALL invoke a real test and record its result.
- 10.6 WHEN a sensitive PSP change requires approval THE SYSTEM SHALL persist an approval request before activation.
- 10.7 THE SYSTEM SHALL return persisted ordered routing rules.
- 10.8 THE SYSTEM SHALL persist authorized draft routing mutations.
- 10.9 THE SYSTEM SHALL validate priority and predicate conflicts before activation.
- 10.10 IF routing validation fails THEN THE SYSTEM SHALL retain the previous active ruleset.
- 10.11 THE SYSTEM SHALL return supported operations from PSP- and state-scoped capability data.
- 10.12 THE SYSTEM SHALL require maker-checker approval for PSP credential activation and routing activation.

## REQ-11: API Clients and Webhooks

- 11.1 THE SYSTEM SHALL return persisted API clients within authorized scope.
- 11.2 THE SYSTEM SHALL persist authorized API-client name, scopes, tenant scope, and IP policy.
- 11.3 WHEN a client secret is first issued or rotated THE SYSTEM SHALL expose it only through a one-time backend response.
- 11.4 THE SYSTEM SHALL never return stored API-client secrets in later reads.
- 11.5 THE SYSTEM SHALL invalidate revoked API-client credentials.
- 11.6 THE SYSTEM SHALL return persisted outbound webhook endpoints.
- 11.7 THE SYSTEM SHALL persist authorized outbound endpoint mutations.
- 11.8 THE SYSTEM SHALL return persisted delivery attempts and operational fields.
- 11.9 THE SYSTEM SHALL enqueue a real eligible replay operation.
- 11.10 THE SYSTEM SHALL distinguish inbound PSP callback operations from Admin-managed outbound endpoints.
- 11.11 IF replay is ineligible THEN THE SYSTEM SHALL leave delivery history unchanged and return its reason.
- 11.12 THE SYSTEM SHALL require maker-checker approval before API-client secret rotation executes.

## REQ-12: Approvals, Audit, and Notifications

- 12.1 THE SYSTEM SHALL return persisted approval requests within authorized scope.
- 12.2 THE SYSTEM SHALL persist approval decisions, actor, reason, and timestamp.
- 12.3 IF an approval is no longer pending THEN THE SYSTEM SHALL reject the stale decision.
- 12.4 THE SYSTEM SHALL return immutable audit records.
- 12.5 THE SYSTEM SHALL support backend audit filtering by actor, action, resource, result, tenant, and date range.
- 12.6 THE SYSTEM SHALL redact credentials, tokens, passwords, personal identifiers, and payment secrets from audit payloads.
- 12.7 THE SYSTEM SHALL NOT expose an API that updates or deletes immutable audit records.
- 12.8 THE SYSTEM SHALL return persisted notification rules.
- 12.9 THE SYSTEM SHALL persist authorized notification-rule mutations.
- 12.10 THE SYSTEM SHALL return persisted notification delivery results.
- 12.11 IF notification delivery fails THEN THE SYSTEM SHALL record a sanitized failure without sensitive destination data.
- 12.12 THE SYSTEM SHALL require maker-checker approval for PSP credentials, routing activation, API-client secret rotation, and refunds.
- 12.13 THE SYSTEM SHALL prevent a maker from deciding their own request.
- 12.14 WHEN a governed request is created, decided, or executed THE SYSTEM SHALL append an immutable audit record linking request, actors, resource version, outcome, and correlation ID.

## REQ-13: Reconciliation and Reports

- 13.1 THE SYSTEM SHALL return real grouped status, currency, count, and total reconciliation values.
- 13.2 THE SYSTEM SHALL apply tenant, period, PSP, channel, and originator filters at the backend.
- 13.3 THE SYSTEM SHALL derive totals and breakdowns from the same filtered dataset.
- 13.4 THE SYSTEM SHALL return real PSP, payment-channel, and originator breakdowns.
- 13.5 THE SYSTEM SHALL preserve exact monetary values at API boundaries.
- 13.6 THE SYSTEM SHALL export values matching the active backend query.
- 13.7 IF a report source is partially unavailable THEN THE SYSTEM SHALL identify the unavailable section without mock substitution.

## REQ-14: Tenants and Originators

- 14.1 THE SYSTEM SHALL return server-paginated merchants within authorized Admin scope.
- 14.2 THE SYSTEM SHALL return persisted tenant profile, status, configuration summary, and related access scope.
- 14.3 THE SYSTEM SHALL persist authorized tenant provision and update operations.
- 14.4 THE SYSTEM SHALL persist authorized tenant suspension and reactivation.
- 14.5 THE SYSTEM SHALL return persisted branch, agent, broker, staff, and application originators within scope.
- 14.6 THE SYSTEM SHALL persist authorized originator create, update, enable, disable, and delete operations.
- 14.7 THE SYSTEM SHALL use stable originator identifiers in transaction, report, audit, and routing relations.
- 14.8 IF an originator remains referenced THEN THE SYSTEM SHALL deactivate it instead of removing historical identity.

## REQ-15: Shared Backend Contract

- 15.9 THE SYSTEM SHALL preserve exact decimal money values received as JSON numbers or decimal strings.
- 15.10 THE SYSTEM SHALL expose safe correlation identifiers in error details when provided.
- 15.11 THE SYSTEM SHALL NOT log session cookies, CSRF values, credentials, tokens, PII, or full payment payloads.
- 15.12 IF a dependency is unavailable THEN THE SYSTEM SHALL return a retryable coded failure without fabricated state.
- 15.13 WHEN a financial or credential-changing mutation is sent THE SYSTEM SHALL require an idempotency key and SHALL return the prior result for replay of the same key and intent.
- 15.14 WHERE a resource supports concurrent mutation THE SYSTEM SHALL return a version or `ETag` and SHALL require it on mutation.
- 15.15 IF a mutation uses a stale version or conflicts in flight THEN THE SYSTEM SHALL return `409` and leave persisted state consistent.

## REQ-16: Verification and Delivery Gates

- 16.3 THE SYSTEM SHALL include backend tests for authorization, validation, persistence, conflict, and not-found behavior of every added operation.
- 16.6 WHEN OpenAPI generation runs THE SYSTEM SHALL include every backend operation required by REQ-3 through REQ-14.
- 16.8 WHEN the backend gate runs THE SYSTEM SHALL pass build, unit tests, integration tests, architecture tests, formatting checks, and secret scan configured by `pol-core`.
- 16.13 IF a required verification command cannot run THEN THE SYSTEM SHALL keep the related task incomplete and record the exact blocker.

## Fixed Decisions

- Existing area-first paths, operation IDs, handlers, aggregate owners, `ProvisioningCoordinator`,
  `MerchantUserInvitation`, and merchant public routes remain canonical.
- `dual-console` extends only existing Commerce operations plus `ListMerchantUsers` and
  `GetMerchantUser`; pure Admin and pure Merchant operations keep pinned policies.
- New writes use owner-local Unit of Work and outbox. No second cross-context coordinator.
- Transaction is a query projection, not a second ledger or aggregate.
- PSP/payment capability must be real. Current adapters with capability `false` get no fake action.
- SQL integration may be skipped only when required local test principals are unavailable; evidence
  records blocker without printing secret values.
