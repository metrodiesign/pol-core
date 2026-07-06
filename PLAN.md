# Plan: Admin Account Management — six additive management endpoints

_Locked via grill — by Claude + KinG (2026-07-05)_

Full EARS spec: `.ai/specs/admin-account-management/requirements.md` (7 REQ / 37
criteria, analyze-audited with 11 logged findings). This plan is the reviewable
condensation; the spec file is the durable source of truth.

## Goal

Give the internal Admin Console a complete management surface over admin accounts
via API — today invite/suspend/tenant-assign/set-roles exist, but operators cannot
list or view admin accounts, cannot reactivate a suspended admin (manual DB edit),
cannot inspect or revoke another admin's sessions, and cannot view another admin's
effective permissions. Close exactly those gaps (already-recorded targets in
`docs/reference/platform-modules.md` §3.1/§3.2) with six purely additive endpoints
under `/api/v1/admins`, reusing the existing permission catalog, session store, and
audit infrastructure. Zero schema migration.

## Approach

1. Spec workflow (constitution): requirements.md is written and analyze-audited;
   next `design.md` (+ traceability), then `tasks.md` (~5-6 vertical slices), then
   implement task-by-task with Evidence. Feature branch off `develop`, single PR.
2. `GET /api/v1/admins` — SFS list (clone the `AdminRoleSfs` exemplar): new
   `AdminAccountSfs` whitelist — filter `email` (eq/ne/in/not_in/like/ilike/contains),
   `tier`,`status` (eq/in, lowercase wire values, strict-parse 400 on bad value),
   sort `email`,`createdAt`, search `email`. `ApplySort` appends `ThenBy(a => a.Id)`
   after EVERY surviving sort set (not just the default) so paging is stable even
   when `email`/`createdAt` collide; default sort `createdAt` desc + id. Unknown
   fields/operators silently dropped per the mandatory SFS convention. Item:
   adminId, email, tier, status, createdAt, subject-bound flag. ROUTE: mapped on
   `api.MapGet("/admins")` NOT the `admin` group — a group's empty-string root
   pattern renders the forbidden trailing slash `/api/v1/admins/` (same reason the
   existing `POST /admins` sits on `api`, Program.cs:1307); auth+permission metadata
   applied per-endpoint.
3. `GET /api/v1/admins/{id:guid}` — detail (on the `admin` group — non-empty pattern,
   fine): list fields + accessible tenants mirroring `GET /me` shape
   (`isUnrestricted` for Super, else `{tenantId, code}` pairs) + role codes of ALL
   assigned roles (including Inactive ones). Every admin-id route carries the
   `:guid` constraint so it never shadows the literal `/me`,`/roles`,`/permissions`
   siblings.
4. `POST /api/v1/admins/{id:guid}/reactivate` — new `AdminAccount.Reactivate()`
   domain method + new `ReactivateAdminCommand`/handler (NOT host-composed). On the
   Suspended→Active transition the handler, inside ONE
   `IUnitOfWork.ExecuteInTransactionAsync`: calls
   `IAdminSessionStore.RevokeAllForAdminAsync` (fresh-login guarantee), flips status
   via the domain method, stages the audit, and `SaveChangesAsync` — all on the
   shared keyed `"admin"` context so the `ExecuteUpdateAsync` revoke + audit insert
   + status update commit or roll back together. Idempotent 204 when already Active
   (no revocation); audit every accepted call (mirrors suspend). 404 unknown id.
5. `GET /api/v1/admins/{id:guid}/sessions` — unpaged, `issuedAt` desc + `sessionId`
   tiebreak; per session: sessionId, familyId, status (active/superseded/revoked),
   issuedAt, idleExpiresAt, absoluteExpiresAt, createdIp, userAgent, read-time
   `isLive`. Token hashes NEVER on the wire. New read method
   `ListByAdminAsync(adminId)` on the session store. Handler checks account
   existence first → 404 for an unknown admin (empty list only for a real admin
   with no sessions).
6. `DELETE /api/v1/admins/{id:guid}/sessions/{sessionId:guid}` — new
   `RevokeAdminSessionCommand`/handler. Resolves the route admin first (404 if the
   admin id is unknown — `AdminSessions` has no FK to `AdminAccounts`, so a session
   could carry an orphan `AdminAccountId`; never accept a revoke/audit against a
   nonexistent admin). Then loads the session by id, 404 if absent OR its
   `AdminAccountId` ≠ the route admin (no existence leak); revokes the ENTIRE
   rotation family (a single-row revoke leaves the live successor usable), emits a
   structured security-log line carrying `sessionId`/`familyId`/`targetAdminId`/
   `correlationId`, stages the audit — all inside one `ExecuteInTransactionAsync`.
   New store method (find-by-id → revoke that `FamilyId`, composed from the existing
   `RevokeFamilyAsync` UPDATE). Idempotent 204 on an already-revoked family.
7. `GET /api/v1/admins/{id:guid}/effective-permissions` — new query handler that
   FIRST resolves the account (`IAdminAccountRepository.GetByIdAsync`) → 404 if
   unknown (the repo's `ListEffectivePermissionsAsync` returns an empty set for a
   nonexistent id, so existence must be checked explicitly), THEN returns the union
   of keys from that admin's ACTIVE roles (identical rule to `/me`); flat array,
   ordinal-sorted; works for suspended targets.
8. Host wiring: the LIST on `api`, the five id-routes on the `admin` group (CSRF
   filter inherited, exempts the GETs, guards the unsafe reactivate/DELETE); reads
   gated `RequirePermission(user.view)` (key exists in the catalog — no migration),
   lifecycle/session ops gated `RequireAdminTier(Super)` mirroring suspend; full
   OpenAPI metadata per repo convention; every mutation is a Mediator command using
   `[FromKeyedServices("admin")] IUnitOfWork` + `ExecuteInTransactionAsync` — the
   host composes nothing; wire DTOs host-local with explicit enum→lowercase
   projection.
9. Tests, three existing layers: Admin.Tests (domain Reactivate guard, handlers
   with fakes, AdminAccountSfs whitelist + stable-sort), Hosts.Tests (401/403
   gates, list root maps with NO trailing slash, route-scheme conventions cover the
   six routes). Integration.Tests ARE added (not optional) — the load-bearing bugs
   live in EF `ExecuteUpdateAsync` + transaction + route binding, which the fakes
   cannot catch: (a) reactivate commits revoke+status+audit atomically and rolls
   ALL back on a forced failure, (b) revoke-family ownership 404 (incl. unknown
   route admin) + cross-family isolation + idempotency, (c) list root has no
   trailing slash, (d) the revoke security-log line carries
   sessionId/familyId/targetAdminId/correlationId. Update
   `docs/reference/platform-modules.md` §3.1/§3.2 rows + `docs/reference/admin-module.md`
   as the final task, including the role-composition note (below).

## Key decisions & tradeoffs

- **Authorization split**: the three READS are gated on the `user.view` permission,
  while reactivate + session ops are gated on `AdminTier.Super` (mirrors the
  existing suspend/invite/tenant-assign gates). `RequirePermission` is a
  single-key filter; a `user.roles` holder does NOT implicitly get `user.view`, so
  an operator who must both see the directory AND assign roles needs a role
  granting BOTH keys. That is a role-composition guideline (documented in the docs
  task), NOT an OR-gate — adding a two-key OR filter is new authz infra rejected as
  over-engineering. Alternative (all reads Super-only) also rejected: breaks the
  role-assignment UX and the RBAC direction.
- **Cross-tenant directory visibility**: any `user.view` holder — even Scoped tier —
  sees ALL admin accounts (emails included). Deliberate: admin accounts are
  control-plane data; the `IAdminQuery` seam only governs tenant BUSINESS reads.
- **Revoke = whole rotation family**, addressed by any session id in it. A
  single-session revoke is security theater (the rotated successor stays live).
  404 (not 403) when the session belongs to another admin — no existence leak.
- **Audit granularity — no migration**: `AdminAccountAudit` has no free-form/session
  column (fields: action, actorType, actorId, targetAdminId, tenantId, targetRoleId,
  correlationId, occurredAt). A session-revoke audit row records WHO revoked sessions
  for WHOM and WHEN (action=`session-revoke`, actorId, targetAdminId, correlationId).
  WHICH session/family is answered by the structured security log line emitted with
  the same correlationId — deliberately NOT via a schema migration (zero-migration
  scope). New `AdminAuditAction` constants (`reactivate`, `session-revoke`) are
  code-only.
- **Reactivate revokes all target sessions first** (fresh-login guarantee): blocks
  stolen-cookie resumption after a suspend/reactivate cycle. Suspend itself stays
  untouched (additive-only constraint).
- **SFS convention compliance**: unknown filter/sort fields are silently dropped
  (logged debug) per the mandatory convention; only malformed JSON and invalid
  VALUES (wrong JSON type, out-of-domain enum string) 400. Initially drafted as
  400-for-unknown-field; corrected against `AdminRoleSfs`/convention doc.
- **Zero migration**: reuse `user.view` permission key, existing tables. No new
  permission (e.g. `user.sessions`) — Super-tier gate covers session ops;
  revisit only if a finer grant is ever needed.
- **No hard DELETE / email edit / tier change**: platform target design keeps
  lifecycle = suspend/reactivate; audits are append-only FK'd to accounts.
- **Sessions list unpaged**: bounded by the existing prune job; SFS there is
  overkill.
- **Detail shows ALL assigned roles (incl. Inactive)** while effective-permissions
  computes from ACTIVE roles only — assignment truth vs enforcement effect, same
  split the resolve pipeline already uses.

## Risks / open questions

- **Transaction composition — RESOLVED, now a design constraint (not a question)**:
  the session store, account repo, and audit writer are ALL bound to the one keyed
  `"admin"` `ProducerDbContext` (`AdminHostWiring.cs:177-188`) whose keyed
  `IUnitOfWork` is `AdminProvisioningUnitOfWork` (`AdminScopedServices.cs:77`).
  `RevokeAllForAdminAsync`/`RevokeFamilyAsync` are `ExecuteUpdateAsync` statements
  that execute IMMEDIATELY on that context's connection — so they enroll in the
  ambient transaction opened by `ExecuteInTransactionAsync` and commit/roll back
  atomically with the change-tracked audit insert. Design ENFORCES: every session
  mutation runs inside the command handler's transaction lambda; the host composes
  nothing. Atomicity is INHERITED from `ExecuteInTransactionAsync` — the same
  primitive every existing admin mutation (Suspend/AssignTenant/role CRUD) uses
  identically — and was verified against the code by the spec-architect review; the
  integration suite covers the new store SQL (list scoping/order, family-revoke
  isolation), NOT a bespoke handler-level rollback test (the raw-SQL harness does
  not boot the EF/DI transaction stack). A forced-rollback end-to-end test remains a
  known coverage gap, accepted as low-risk given the reused primitive.
- **Revoke racing an in-flight rotation** can leak a successor in a narrow window —
  inherited `RevokeFamilyAsync` semantic (same as logout/reuse-detection), accepted
  and documented in REQ-5.3; store hardening explicitly out of scope.
- `RouteSchemeConventionTests` enumerates routes — the six new entries must satisfy
  it (all under `/api/v1/admins`, per-endpoint `RequireAuthorization`).

## Out of scope

- Frontend admin SPA work (backend API only).
- Changing suspend behavior (session revocation on suspend), maker-checker
  ChangeRequest flow, moving `AdminAllowlist` config → DB, hard DELETE, email/tier
  mutation, session-store race hardening, SFS/paging on the sessions list, new
  permission keys, any schema migration.
