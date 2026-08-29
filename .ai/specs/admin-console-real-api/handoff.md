> Status: unknown
# Handoff Note: Admin Console Real API Backend Contract

## Task Summary

Backend mirror for approved, source-reconciled `pol-admin` real API integration across 23 product
modules. This mirror owns `pol-core` behavior only; frontend-only behavior stays in `pol-admin`.

## Current Status

- Source Admin requirements/design/tasks approved and fresh-context reviewer returned `READY`.
- Reconciliation baseline: `pol-core@83c86cb`, `pol-merchant@8191b44`.
- Mirror artifacts approved and gated; Tasks 1–9 complete. Signed-in Browser gate passed across the
  real Admin API, including Commerce and Reporting, while preserving the original Policy UI.
- Current branch: `codex/admin-real-api-integration`.
- Existing `pol-admin` dirty baseline belongs to prior work and must not be reverted.

## Root-Cause Constraints

- Existing Commerce and Merchant-user read routes are pinned to Merchant authentication; Admin portal
  needs an actor branch, not a second endpoint or minted Merchant session.
- Existing Admin spec had proposed duplicate coordinator/invitation/operation ownership; reconciliation
  removed them and preserved canonical owners.
- ControlPlane and MerchantRuntime are different DbContexts; delivery secrets therefore live in
  ControlPlane while PSP secrets stay MerchantRuntime.
- Generic idempotency across DbContexts cannot recover commit-unknown safely; each owner keeps its own
  operation record and outbox.

## Fixed Decisions

- `dual-console` covers existing Commerce plus `ListMerchantUsers`/`GetMerchantUser` only.
- New platform permissions: `txn.manage`, `merchants.users.manage`, `merchants.roles.view`,
  `merchants.roles.manage`.
- Existing `ProvisioningCoordinator`, `MerchantUserInvitation`, public Merchant routes, operation IDs,
  handlers, and success contracts remain canonical.
- Governance decides through outbox; target owner executes idempotently.
- Transaction remains query projection; unsupported PSP capabilities remain unavailable.

## Task 1 Evidence

- IAM 61/61, Hosts 465/465, Architecture 233/233.
- EF model has no pending changes; permission migration forward/rollback scripts build and use
  deterministic GUIDs.
- Scoped format gate passes. Full-repo format still reports unrelated pre-existing whitespace.
- SQL integration is blocked before DB access by absent `POL_APP_PASSWORD`; no credential guessed.
- Existing Commerce/Merchant-user route policies remain unchanged until Tasks 5/6.

## Task 2 Evidence

- Governance persistence, typed owner protocol, outbox, operation ledger, six Admin endpoints,
  append-only redacted hash chain, and signed external checkpoint verifier are implemented.
- Governance 6/6, Architecture 240/240, Hosts 472/472, solution build 0 warnings/errors, EF no model
  drift, scoped format, secret scan, migration scripts, and both spec traces pass.
- Development host applied both migrations and executed live ControlPlane queries. Credentialed
  integration harness remains unavailable because `POL_APP_PASSWORD`/`POL_SA_PASSWORD` are unset.
- Admin frontend Governance source passes 277/277 tests, typecheck, lint, and production build.
- Signed-in Browser rendered live Approval and Audit queues, server paging/filter controls, and genuine
  empty states without runtime errors. No fake rows or auth bypass were created; detail remains covered
  by host/frontend tests because the live database is empty.
- Root cause fixed: backend wire names are `accessibleMerchants.merchants` plus `permissions`; frontend
  expected stale `accessibleTenants.tenants`, so its strict parser rejected a valid 200 session.

## Task 3 Evidence

- Persisted `Version` now covers Admin User, IAM Role, Office, Division, Position, and Level through
  one migration. EF reports no pending model change; forward and rollback SQL contain six matching
  add/drop operations.
- Existing paths and operation IDs remain canonical. Lists page server-side; detail returns ETag;
  mutations require `If-Match`; stale writes return `state_conflict`; session revoke replays through
  the durable Admin operation ledger.
- Backend gates pass: Admins 98/98, IAM 63/63, master suites 28/28, persistence 3/3, Task 3 Host
  contracts 11/11, UTC Host regression 2/2, solution build, and scoped format.
- Live Browser loaded all six modules through real API. Office create, edit, deactivate, and reload
  persisted successfully with no mock or auth bypass.
- Runtime evidence isolated one cross-repo failure: SQL Server `datetime2` restored `CreatedAt` with
  `DateTimeKind.Unspecified`, so JSON omitted a timezone and the frontend explicit-instant validator
  rejected the `200` response. The shared HTTP JSON boundary now emits persisted UTC values with `Z`.

## Tasks 4–6 Evidence

- Tenant, Originator, PSP, Routing, merchant-user, merchant-role, Product, Cart, Order, and
  PaymentSession contracts are implemented at their existing owners. Existing routes/operation IDs
  remain canonical; Admin access uses the explicit audience branch and never mints a Merchant session.
- Owner-local migrations, operation ledgers, outboxes, staged Vault material, ETag/idempotency, and
  least-privilege runtime grants are applied. Live grant parity reports zero missing grants.
- Signed-in Browser passed tenant/Originator/PSP/Routing controls, merchant users/roles, 25 real Product
  rows, Cart add/remove, and Buy-now review. Original `/policy/list` action UI remains present: 25
  `ซื้อ … เลย` plus 25 `เพิ่ม … ลงตะกร้า`; no mock fallback or auth bypass exists.

## Tasks 7–8 Evidence

- Query-only dashboard/transaction/report projections, bounded exports, Orders reconciliation,
  API-client lifecycle, inbound events, SSRF-safe webhooks, notification rules/logs, and one-time
  secret reveal are complete. No Transaction aggregate or duplicate coordinator was introduced.
- Live SQL exposed two provider-only translation defects. Reusable Reporting projections now avoid
  composing on positional DTO constructors; item counts run as an authorized grouped query after
  paging; reconciliation sorts group keys before terminal projection. Provider regressions cover
  dashboard/list/detail, 100,001 export IDs, tenant scope, exact item count, and reconciliation.
- Signed-in Browser renders transactions, reconciliation, and reports without load errors, and passed
  API-client create/reveal/update/revoke, outbound endpoint create/disable/delete, and notification
  rule create/toggle/edit/delete. Rotation stays maker-checker-tested; replay stays provider-tested
  because no eligible failed delivery exists.

## Task 9 Evidence

- Backend: build 0 warnings/errors; full solution 1,865/1,865; Architecture 264/264;
  Integration 147/147; Hosts 491/491; secret scan, diff check, and Core trace 156/156 pass.
- Frontend: 261/261 tests, typecheck, lint without errors, 114-route production build, production
  dependency audit with zero vulnerabilities, Admin trace 192/192, zero protected runtime mock
  imports, UI-preservation test, and signed-in Browser smoke pass.
- `pol-merchant@8191b44` remains unchanged; its approved contract and public Merchant flows were
  consumed, not duplicated.

## Next Steps

1. Review the final dirty worktree and commit it on `codex/admin-real-api-integration`.
2. Push through a PR; do not push directly to `main` or `develop`.
