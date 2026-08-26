# Implementation Tasks: Admin Console Real API Backend Contract

> Status: unknown

ทำตามลำดับ 1 → 9. แต่ละ task ต้องจบ owner code, migration, targeted tests, OpenAPI และ
Evidence ก่อนเริ่ม task ถัดไป. ห้าม stub success, duplicate route/aggregate, หรือ coordinator ใหม่.

- [x] 1. Admin session and `dual-console` delivery spine — เพิ่ม platform permission keys/seeds,
  deterministic audience selection, paired permission, audience CSRF, OpenAPI OR และ isolated tests.
  Existing Commerce plus `ListMerchantUsers`/`GetMerchantUser` ยัง pinned จน Task 5/6 เพิ่ม Admin
  owner branchพร้อม route adoption; pure-audience routesไม่เปลี่ยน.
  - Satisfies: REQ-1.7–1.8, REQ-1.10, REQ-2.1–2.2, REQ-2.7–2.11, REQ-2.13–2.15, REQ-15.10–15.11, REQ-16.3.
  - Ownership: Host/Iam only; permission seed is sole persistence delta.
  - Verify: isolated Admin/Merchant/both-cookie/invalid-Admin tests, wrong-side permission/CSRF denial, boot parity, OpenAPI security OR, existing route policy preservation, offline Host/Iam tests.
  Evidence:
  - Added 26-key canonical IAM catalog, deterministic data-only migration, endpoint-aware
    `ConsoleSession`, paired audience permission/CSRF gates, and OpenAPI security OR. Existing owner
    routes remain on their original single-console policies.
  - `dotnet test tests/Iam.Tests/Iam.Tests.csproj --no-restore` — 61/61;
    `Hosts.Tests` — 465/465; `Architecture.Tests` — 233/233.
  - `dotnet ef migrations has-pending-model-changes` — no changes. Forward and reverse migration
    scripts build, run in a transaction, and contain deterministic `f900...001`–`004` grant IDs.
    Scoped `dotnet format --verify-no-changes --include ...` passes for every Task 1 C# file.
  - Deviation: selected SQL integration tests stop before DB access because
    `POL_APP_PASSWORD` is absent. Full-repo format remains red only on pre-existing whitespace
    outside scoped Task 1 files. Both blockers are recorded; no secret or unrelated formatting edit.

- [x] 2. Governance and immutable audit foundation — เพิ่ม approval/audit persistence,
  `admin.OperationRecords`, owner-request → decision → owner-execution outbox protocol,
  maker-checker/version checks, append-only hash/redaction, and query endpoints. Governance never
  writes target contexts.
  - Satisfies: REQ-12.1–12.7, REQ-12.12–12.14, REQ-15.10–15.15.
  - Depends on: 1.
  - Verify: maker denial, stale decision, operation replay/mismatch, inbox/outbox idempotency, audit append/hash/redaction/tamper, migration, OpenAPI.
  Evidence:
  - Source complete: Governance Domain/Application/Infrastructure, ControlPlane persistence,
    `admin.OperationRecords`, typed request/decision/execution events, owner outbox, maker/version/
    permission checks, idempotent decision replay, append-only redacted hash chain, and signed external
    checkpoint verifier. Governance never resolves or writes a target context.
  - Added six Admin-only operations at canonical `/api/v1/approvals` and `/api/v1/audits` paths with
    CSRF, permissions, ETag/`If-Match`, `Idempotency-Key`, stable Problem Details, paging, and OpenAPI.
  - `Governance.Tests` 6/6; `Architecture.Tests` 240/240; `Hosts.Tests` 472/472; solution build passes
    with 0 warnings/errors. EF reports no pending model changes. Forward/rollback scripts are
    transactional; `AuditRecords` grants only SELECT/INSERT and no UPDATE/DELETE.
  - Root-cause gate fixes: added Governance migration assembly to the model-disjointness fixture;
    classified three ControlPlane-only transactions; isolated raw SQL locks in a narrow allowlisted
    port; made audit-anchor config resolve after host configuration layering; registered Governance
    paths in canonical area/CORS/permission inventories. Targeted regressions and full suites pass.
  - Scoped format passes. Full-repo format remains red on pre-existing whitespace outside Task 2.
    Secret scan passes; Core/Admin spec trace covers 156/156 and 192/192. Local Development host
    applied both Governance migrations and executed live ControlPlane queries without logging values.
  - Signed-in Browser verification passed against live API at `/control/approvals` and
    `/control/audit`: both protected routes rendered server-paged filter controls and genuine empty
    results without console/runtime errors. No fake records or auth bypass were added; detail behavior
    remains covered by host/frontend tests because the live database contains no rows.
  - Browser gate exposed and fixed one real cross-repo contract mismatch: backend `GetAdminMe` returns
    `accessibleMerchants.merchants` plus `permissions`, while the frontend validator expected stale
    `accessibleTenants.tenants`; the API returned 200 but parsing raised `TypeError`. Contract parser,
    type, and regression test now match the named backend/OpenAPI response.
  - `POL_APP_PASSWORD` and `POL_SA_PASSWORD` remain unset, so the credentialed integration harness was
    not run.

- [x] 3. Admin identity, roles, and organization masters — extend existing Admin/Iam/Office/
  Division/Position/Level handlers with one-page server pagination, detail, ETag, stable conflicts,
  idempotent session revoke, and unchanged operation IDs.
  - Satisfies: REQ-1.1–1.2, REQ-7.1–7.11, REQ-9.1–9.6, REQ-9.8, REQ-15.13–15.15.
  - Depends on: 1, 2.
  - Verify: scope/tier/role/session/paging/status/concurrency tests, migration if needed, OpenAPI, targeted Admin/Iam/master-data suites.
  Evidence:
  - Added persisted resource versions for Admin User, IAM Role, Office, Division, Position, and Level
    through one migration. Forward/rollback SQL contains exactly six `ADD Version`/`DROP Version`
    operations; EF reports no model drift after a real build.
  - Existing operation IDs remain unchanged. Lists use one-page server pagination; detail responses
    return ETag; mutations require `If-Match`; stale writes return stable `state_conflict`; session
    revoke uses the durable Admin operation ledger and `Idempotency-Key`.
  - Targeted backend gates pass: Admins 98/98, IAM 63/63, master-data suites 28/28, persistence 3/3,
    and Task 3 Host contracts 11/11. UTC wire regression adds 2/2 passing Host tests; solution build
    and scoped format pass.
  - Live API applied the migration. Signed-in Browser loaded Admin User, Role, and all four master
    lists from real API. Office smoke passed create, ETag edit, deactivate, and reload with persisted
    status; no mock or auth bypass was used.
  - Root cause found by runtime instrumentation: SQL Server `datetime2` materialized `CreatedAt` as
    `DateTimeKind.Unspecified`, so JSON omitted an offset and the strict frontend rejected a valid
    `200` page. One HTTP JSON converter now serializes persisted UTC timestamps with explicit `Z`.

- [x] 4. Tenant, Originator, PSP, and Routing control plane — add tenant list/update/status,
  five Originator kinds, PSP configuration/test, staged MerchantRuntime Vault versions, routing drafts,
  governed activation, active-ruleset selection, and `txn.AdminOperationRecords`.
  - Satisfies: REQ-10.1–10.12, REQ-14.1–14.8, REQ-15.9–15.15.
  - Depends on: 1, 2.
  - Ownership: `ProvisioningCoordinator` remains exclusive to `ProvisionMerchant`; all new writes owner-local plus outbox.
  - Verify: tenant isolation, originator references, ETag/idempotency, secret no-read, test result, routing overlap/eligibility/default, stage/recovery, approval round trip, migration, OpenAPI.
  Evidence:
  - Added owner-local tenant/Originator/PSP/Routing stores, staged Vault versions, durable Admin operation
    ledger, approval outbox, active/default routing selection, exact ETag/idempotency/OpenAPI contracts,
    and narrow Admin write capabilities. `ProvisioningCoordinator` remains exclusive to provisioning.
  - Live signed-in Browser passed tenant edit/restore; Originator create/edit/disable/enable/delete;
    PSP probe failure persistence; Routing create/edit/delete and activation request. Test Routing rule was
    disabled, so it cannot send payment traffic; pending approval is
    `019febb9-c6f8-7411-bbac-3f217f401fef` at ruleset version 3.
  - Four runtime defects were reproduced before correction: cross-merchant detail reads were hidden by
    `CurrentMerchant` query filters; Merchant status serialized CLR enum casing; missing Vault material
    escaped before the PSP probe catch; replacement Routing children with client-minted Guid IDs were
    tracked `Modified`, producing a zero-row `UPDATE` and false 409. Narrow owner fixes plus regressions
    cover each path; Routing child mapping now follows the existing `CartItem` `ValueGeneratedNever`
    pattern in both runtime and migration-owner models.
  - Full gates: solution build 0 warnings/errors; Architecture 242/242, Merchants 177/177, Payments
    263/263, Hosts 481/481. EF reports no pending model changes; Core/Admin `git diff --check` pass.
    Frontend typecheck, lint, production build (114 routes), and tests 232/232 pass.

- [x] 5. Merchant users and merchant-owned roles — extend existing `ListMerchantUsers` and
  `GetMerchantUser` through Task 1 policy while preserving IDs/handlers; add Admin invitation/update
  and merchant-owned role APIs. Reuse canonical invitation aggregate/table/outbox and exact consume rules.
  - Satisfies: REQ-2.16, REQ-8.1–8.14, REQ-15.10–15.15.
  - Depends on: 1.
  - Ownership: MerchantUser owner-local allowlist and `merch.AdminUserOperationRecords`; no raw token or new invitation store.
  - Verify: Admin/Merchant scope, masked reads, invitation email/tenant/expiry/revoke/replay/atomic consume, lifecycle, roles, last-manager, stale decision, OpenAPI.
  Evidence:
  - Dual-console list/detail preserve existing operation IDs; Admin edit/invite/lifecycle and
    merchant-owned role surfaces use audience-specific permissions, CSRF, ETag, and idempotency contracts.
  - Migration `20260810133139_AdminMerchantIdentityControl` adds invitation audience/role metadata,
    Merchant-user versioning, and owner-local Admin operation ledger; migration applied to local SQL.
  - Runtime root causes fixed at owners: SQL Server canonical Guid values may carry version nibble `0`,
    while the Admin client incorrectly required RFC versions `1`–`8`; role reassignment legitimately
    deletes old assignments, while an old write-floor test still rejected every assignment delete.
  - Signed-in production Browser passed real user list/detail, role list, shared-role mutation guards,
    canonical `merchant_staff` invitation, and exact 375/768/1440 no-overflow checks. Development uses
    `CaptureInvitationEmailSender`, so the test invitation sent no external email and exposed no token.
  - Gates: Hosts 482/482; Admin tests 240/240; lint, typecheck, and production build 114 routes pass.

- [x] 6. Admin policy-to-payment-link and Order lifecycle — add `/products/documents`; extend
  existing Cart/Order/PaymentSession operations through Task 1 policy with explicit merchant/originator,
  audience-aware request schemas, routing selection, durable operation state, hosted link, lifecycle,
  and Order export. Preserve Merchant success bodies and operation IDs.
  - Satisfies: REQ-4.1–4.6, REQ-4.8–4.10, REQ-4.14–4.16, REQ-5.1–5.7, REQ-5.9–5.14, REQ-15.9–15.15.
  - Depends on: 1, 4.
  - Verify: audience auth/CSRF/permission, scope, Product filters/payability, request `oneOf`, replay, ETag, routing, cancel/resend/capability/export, OpenAPI, owner suites.
  Evidence:
  - Added the Admin Product projection and extended the existing Cart, Order, and PaymentSession
    owners with explicit merchant/originator scope, audience-specific OpenAPI `oneOf`, ETag,
    idempotency, routing selection, hosted link, lifecycle actions, and bounded Order export. Existing
    Merchant paths, operation IDs, handlers, validation, and success bodies remain canonical.
  - Migrations `20260810150130_AdminCommerceLifecycle`,
    `20260810153008_AdminCommerceUpdatedAtDefault`, and
    `20260810162000_AdminCommerceOperationUpdateGrant` provide owner-local operation persistence and
    least-privilege runtime grants. Live migration parity reports no missing runtime grants.
  - `AdminTask6ContractTests`, commerce-operation, payment-session, routing-selector, and owner suites
    pass as part of the 1,864-test solution gate. Signed-in Browser loaded 25 real Product rows; add,
    remove, Buy-now review, and cancel flows passed without mock fallback. Original action buttons and
    layout remain unchanged: 25 `ซื้อ … เลย` and 25 `เพิ่ม … ลงตะกร้า` actions are present.

- [x] 7. Dashboard, Transactions, Reconciliation, and Reports — add query-only dashboard,
  transaction projection/detail/capability, bounded transaction/order/report exports, and operations
  reports. Extend existing `Orders` reconciliation only; create no Transaction aggregate.
  - Satisfies: REQ-3.1–3.4, REQ-6.1–6.14, REQ-13.1–13.7, REQ-15.9–15.15.
  - Depends on: 1, 6.
  - Verify: authorized projections, filters, totals/breakdowns same dataset, decimal strings, export bounds/formula escaping, capability=false for unsupported adapters, refund unknown-state/approval/audit, OpenAPI.
  Evidence:
  - Added query-only Reporting projections for dashboard, transactions, detail/capability, bounded
    CSV exports, and operations reports. Reconciliation remains an Orders read; no Transaction
    aggregate or second financial owner was created. Unsupported adapter actions remain unavailable.
  - Provider-backed SQL Server regressions execute dashboard/list/detail, exact item counts, the
    100,001-row export sentinel, tenant scope, and reconciliation. Root causes fixed at reusable query
    seams: composed positional projections were not SQL-translatable; reusable projections now use
    member initialization, item counts run as a scoped grouped query after paging, and reconciliation
    orders group keys before its terminal DTO projection.
  - `AdminReportingReaderIntegrationTests` 3/3, Integration 148/148,
    `AdminTask7ContractTests`, Architecture 264/264, and Hosts 490/490 pass. Signed-in Browser renders
    `/transaction/list` and `/control/reconciliation` from the restarted live API without load errors.

- [x] 8. API clients, Webhooks, and Notifications — add API-client lifecycle and one-time reveal,
  inbound event query, SSRF-safe outbound endpoints/delivery/replay, notification rules/logs, and
  ControlPlane-owned `admin.DeliverySecretVersions`. Reuse Task 2 Governance protocol.
  - Satisfies: REQ-11.1–11.12, REQ-12.8–12.11, REQ-15.10–15.15.
  - Depends on: 1, 2, 4.
  - Verify: secret no-read/log, one-time no-store ticket, constant-time check, stage/activate/compensate/commit-unknown replay, Notification idempotency header/OpenAPI/replay, SSRF re-resolution/pinned connect/no redirect, replay eligibility, sanitized failures, migration.
  Evidence:
  - Added API-client lifecycle and one-time no-store reveal, inbound-event metadata query,
    SSRF-restricted webhook endpoints/delivery/replay, notification rules/logs, and ControlPlane-owned
    delivery secret versions. Reads omit raw credentials and inbound payloads; secret comparison and
    owner-local replay/idempotency paths stay at their canonical owners.
  - Migrations `20260810184403_AdminDeliveryControlAndInboundWebhook` and
    `20260811024015_AdminDeliveryRuntimeGrants` apply required schema and narrow runtime permissions;
    live migration parity reports zero missing grants.
  - `AdminTask8ContractTests`, `DeliverySecurityTests`, transaction-inventory checks, and full
    Architecture/Hosts gates pass. Signed-in Browser passed API-client create/reveal/update/revoke,
    outbound endpoint create/disable/delete, and notification rule create/toggle/edit/delete.
    Rotation stays maker-checker-tested because the maker cannot approve its own request; replay stays
    automated because the local database has no eligible failed delivery.

- [x] 9. Backend-wide contract and release verification — no feature code. Prove exact operation
  inventory, persisted behavior, owner boundaries, security, migrations, and cross-repository trace.
  - Satisfies: REQ-1.1–1.2, REQ-1.6–1.8, REQ-1.10, REQ-16.3, REQ-16.6, REQ-16.8, REQ-16.13.
  - Depends on: 1–8 complete.
  - Verify: format, build, offline tests, SQL integration when credentials exist, architecture tests, OpenAPI contract, secret scan, spec trace for both mirrors, transaction inventory, no new bypass/coordinator/duplicate operation.
  Evidence:
  - `dotnet build pol-core.slnx --no-restore` passes with 0 warnings/errors. Full solution tests pass
    1,865/1,865, including Architecture 264/264, Integration 147/147, and Hosts 491/491 against the
    configured local SQL Server. Core/Admin spec trace passes 156/156 and 192/192; both secret scans
    and `git diff --check` pass.
  - Frontend release gate passes 261/261 tests, TypeScript, lint with no errors, 114-route production
    build, production dependency audit with zero vulnerabilities, zero protected runtime mock imports,
    and the policy UI-preservation regression.
  - Signed-in live smoke covers the protected session shell, real list/detail/control reads,
    Cart add/remove and Buy-now review, reporting queries, and responsive public/auth routes. No auth
    bypass, mock fallback, duplicate coordinator, duplicate aggregate, or duplicate operation was added.

## Safe Execution Order

Run sequentially. Within each task: inspect owner/callers → add failing targeted test → minimum owner
change → migration where required → targeted test → OpenAPI test → full affected solution gate →
Evidence. Stop task incomplete on red gate or unavailable required credential and record exact blocker.
