# Requirements: Admin Account Management

> Status: approved 2026-07-06

## Overview

The Admin Console (internal-only, control-plane — PROJECT_CONTEXT "ทีมกลาง": provision,
monitor, audit) can today invite, suspend, assign tenants, and set roles for admin
accounts, but it cannot LIST or VIEW admin accounts, cannot REACTIVATE a suspended
admin (requires manual DB edits), cannot inspect or revoke another admin's sessions,
and cannot view another admin's effective permissions. All of these are recorded
targets in `docs/reference/platform-modules.md` §3.1 (list/view + session management,
reactivate) and §3.2 (effective permissions). This feature closes those gaps with six
purely additive endpoints under `/api/v1/admins`, reusing the existing permission
catalog and session/audit infrastructure — no new tables, no new permission keys, no
migration. Control-plane only; the payment/funds flow is untouched.

## REQ-1: List admin accounts

**User Story:** As a platform operator holding `user.view`, I want a paged, filterable
list of admin accounts, so that I can manage the operator team through the console
instead of reading the database.

**Acceptance Criteria (EARS):**

- 1.1 WHEN `GET /api/v1/admins` is called by an authorized admin THE SYSTEM SHALL
  return a paged result (`items`, `page`, `limit`, `total`) following the SFS
  convention (`page`, `limit`, `filters`, `sort`, `search` per
  `docs/reference/search-filter-sort.md`).
- 1.2 THE SYSTEM SHALL include for each list item: `adminId`, `email`, `tier`, `status`, `createdAt`, and a flag indicating whether a Google subject is bound (invite still pending = not bound).
- 1.3 THE SYSTEM SHALL accept filter/sort fields only from a deny-by-default whitelist (filter: `email`, `tier`, `status`; sort: `email`, `createdAt`), SHALL append the account id as a final tiebreak to EVERY sort ordering (not only the default) so paging is stable when a sorted column collides, and SHALL apply a deterministic default sort (`createdAt` descending, id tiebreak) when no sort is given.
- 1.4 THE SYSTEM SHALL apply `search` as an escaped substring match on `email`.
- 1.5 IF the SFS query parameters are malformed (invalid `filters`/`sort` JSON, or
  values the parser rejects) THEN THE SYSTEM SHALL respond 400 ProblemDetails.
- 1.8 THE SYSTEM SHALL silently drop (and log by field name at debug level) any filter or sort entry referencing a non-whitelisted field or a disallowed operator, per the SFS convention (`docs/reference/search-filter-sort.md` — unknown structure is dropped; invalid VALUES are 400 per REQ-1.5/1.7).
- 1.6 THE SYSTEM SHALL project `tier` and `status` to the wire as stable lowercase strings (`"super"`/`"scoped"`, `"active"`/`"suspended"`) via explicit projection (no global enum converter).
- 1.7 IF a `tier` or `status` filter value is outside its lowercase wire domain
  (`"super"`/`"scoped"`, `"active"`/`"suspended"`), or any filter value is not a
  JSON string, THEN THE SYSTEM SHALL respond 400 ProblemDetails (strict parse — no
  silent default, mirroring the existing role-status filter).

## REQ-2: View one admin account

**User Story:** As a platform operator holding `user.view`, I want the full detail of
one admin account, so that I can see its lifecycle state, tenant reach, and roles in
one place.

**Acceptance Criteria (EARS):**

- 2.1 WHEN `GET /api/v1/admins/{id}` is called for an existing account THE SYSTEM
  SHALL return `adminId`, `email`, `tier`, `status`, `createdAt`, the subject-bound
  flag, the accessible tenants mirroring the `GET /api/v1/admins/me` shape
  (`isUnrestricted` true for a Super with no tenant list; otherwise the assigned
  `{tenantId, code}` pairs), and the account's role codes — every assigned role,
  including roles whose status is currently Inactive.
- 2.2 IF the id is unknown THEN THE SYSTEM SHALL respond 404 ProblemDetails.

## REQ-3: Reactivate a suspended admin

**User Story:** As a Super admin, I want to reactivate a suspended admin through the
API, so that restoring access does not require manual database edits.

**Acceptance Criteria (EARS):**

- 3.1 WHEN `POST /api/v1/admins/{id}/reactivate` is called for a Suspended account
  THE SYSTEM SHALL set the account status to Active and respond 204.
- 3.2 WHEN a reactivate call is accepted (including the idempotent already-Active
  case) THE SYSTEM SHALL append an append-only `AdminAccountAudit` entry
  (reactivate action, acting admin id, correlation id, target admin id) in the
  same transaction as the status change — mirroring suspend, which audits every
  accepted call.
- 3.3 WHEN `POST /api/v1/admins/{id}/reactivate` is called for an already-Active
  account THE SYSTEM SHALL respond 204 without error (idempotent, mirroring the
  existing suspend semantics).
- 3.4 IF the id is unknown THEN THE SYSTEM SHALL respond 404 ProblemDetails.
- 3.5 WHEN a reactivate call transitions an account from Suspended to Active THE
  SYSTEM SHALL first revoke every non-revoked session of that account, so no
  session issued before or during the suspension survives reactivation (a fresh
  login is required).
- 3.6 WHEN a reactivate call finds the account already Active (the idempotent case,
  3.3) THE SYSTEM SHALL NOT revoke any session.

## REQ-4: List an admin's sessions

**User Story:** As a Super admin, I want to see the sessions of an admin account, so
that I can audit device access and decide what to revoke.

**Acceptance Criteria (EARS):**

- 4.1 WHEN `GET /api/v1/admins/{id}/sessions` is called for an existing account THE
  SYSTEM SHALL return ALL of that account's stored sessions as an unpaged list
  ordered by `issuedAt` descending with session id as a deterministic tiebreak
  (the store's prune job bounds the set).
- 4.2 THE SYSTEM SHALL include for each session: `sessionId`, `familyId`, `status` (`"active"`/`"superseded"`/`"revoked"`), `issuedAt`, `idleExpiresAt`, `absoluteExpiresAt`, `createdIp`, `userAgent`, and a read-time `isLive` flag (Active AND within both idle and absolute windows at the time of the read).
- 4.3 THE SYSTEM SHALL never expose session token material or token hashes on the wire.
- 4.4 IF the admin id is unknown THEN THE SYSTEM SHALL respond 404 ProblemDetails.
- 4.5 WHEN the account has no sessions THE SYSTEM SHALL respond 200 with an empty list.

## REQ-5: Revoke an admin's session

**User Story:** As a Super admin, I want to revoke a specific session of an admin, so
that a stolen or abandoned device loses access immediately without suspending the
whole account.

**Acceptance Criteria (EARS):**

- 5.1 WHEN `DELETE /api/v1/admins/{id}/sessions/{sessionId}` is called for a session
  belonging to that admin THE SYSTEM SHALL revoke every non-revoked session in that
  session's rotation family and respond 204.
- 5.2 WHEN a revoke call is accepted (including the idempotent already-revoked
  case) THE SYSTEM SHALL append an append-only `AdminAccountAudit` entry
  (session-revoke action, acting admin id, correlation id, target admin id) in the
  same transaction as the revocation.
- 5.3 WHILE a session family is revoked THE SYSTEM SHALL reject subsequent requests presenting any cookie of that family (existing per-request session validation), with the same guarantee level as the existing logout family revocation — a rotation racing the revoke in a narrow window is an inherited platform semantic, not widened by this feature.
- 5.4 IF the session id is unknown, or the session does not belong to the admin in
  the route, THEN THE SYSTEM SHALL respond 404 ProblemDetails.
- 5.5 WHEN `DELETE` is called again for an already fully-revoked family THE SYSTEM
  SHALL respond 204 without error (idempotent).

## REQ-6: View an admin's effective permissions

**User Story:** As a platform operator holding `user.view`, I want to see the
effective permissions of an admin account, so that I can verify what a person can
actually do before/after changing their roles.

**Acceptance Criteria (EARS):**

- 6.1 WHEN `GET /api/v1/admins/{id}/effective-permissions` is called for an existing
  account THE SYSTEM SHALL return the distinct union of permission keys granted
  through the account's assigned roles whose role status is Active — the same
  resolution rule the sign-in pipeline uses for `GET /api/v1/admins/me`.
- 6.2 THE SYSTEM SHALL return the keys as a flat array sorted ascending (ordinal) so the response is deterministic.
- 6.3 IF the admin id is unknown THEN THE SYSTEM SHALL respond 404 ProblemDetails.
- 6.4 WHILE the target account is Suspended THE SYSTEM SHALL still return its current role-derived permission set (suspension blocks sign-in, not role grants).

## REQ-7: Authorization, wire, and compatibility guarantees

**User Story:** As the platform owner, I want the new endpoints to sit inside the
existing authorization and API conventions, so that the admin surface stays uniform
and nothing existing changes behavior.

**Acceptance Criteria (EARS):**

- 7.1 THE SYSTEM SHALL require the authenticated admin session policy (and the admin group's CSRF protection for unsafe methods) on all six endpoints; IF the session is absent or invalid THEN THE SYSTEM SHALL respond 401.
- 7.2 THE SYSTEM SHALL gate `GET /api/v1/admins`, `GET /api/v1/admins/{id}`, and `GET /api/v1/admins/{id}/effective-permissions` on the existing `user.view` permission, fail-closed (missing permission or unbound scope → 403).
- 7.3 THE SYSTEM SHALL gate `POST .../reactivate`, `GET .../sessions`, and `DELETE .../sessions/{sessionId}` on `AdminTier.Super` (non-Super → 403), mirroring the existing suspend gate.
- 7.4 THE SYSTEM SHALL reuse only existing permission catalog keys and existing tables — no new permission keys, no new tables, no schema migration.
- 7.5 THE SYSTEM SHALL leave every existing endpoint's route and behavior unchanged (purely additive change).
- 7.6 THE SYSTEM SHALL declare complete OpenAPI metadata (name, summary, description, success and ProblemDetails status codes) for all six endpoints, matching the repo convention.

## Edge Cases & Open Questions

- **Reactivation revokes pre-suspension sessions (REQ-3.5, grill decision
  2026-07-05)**: suspend itself does not revoke sessions (per-request resolution
  blocks a suspended account); reactivate revokes ALL of the target's sessions
  before activating, so a cookie stolen before suspension cannot resume after
  reactivation. The existing suspend flow is untouched (REQ-7.5).
- **Pending invite accounts** (Subject not yet bound): appear in the list with the
  bound flag false; sessions list is empty; effective permissions reflect any
  pre-assigned roles.
- **Super self-revoke of own session family** is allowed and acts as a logout of
  that family — no lockout risk (re-login is possible), unlike self-suspend which
  stays forbidden.
- Exact SFS operator set per whitelisted field, wire field names (e.g. the
  subject-bound flag name), and the audit action strings are design-phase decisions.
- List items intentionally exclude role codes and tenant assignments (kept to the
  detail view, REQ-2) to keep the list query join-free; revisit only if the console
  needs them in the grid.

### Analyze findings log — 2026-07-05, anchor `a5b1274` (file uncommitted at analyze time)

Full audit (/spec-analyze), 9 findings, all decided 2026-07-05:

1. **[Gap, REQ-1.3/1.5] tier/status filter value domain undefined** — DECIDED (a):
   strict lowercase domain, outside → 400. Added REQ-1.7. Mirrors `AdminRoleSfs.ParseStatus`.
2. **[Gap, REQ-2.1] Super has no assignment rows, `tenants` always empty** — DECIDED
   (a): mirror the `GET /me` accessible shape (`isUnrestricted`). Amended REQ-2.1.
3. **[Gap, REQ-5.3] revoke-family racing an in-flight rotation can leak a successor
   (narrow window)** — DECIDED (a): accept as inherited `RevokeFamilyAsync` semantic
   (same as logout); store hardening out of scope (REQ-7.5 additive). Amended REQ-5.3.
4. **[Gap, REQ-4.1] sessions list unpaged** — DECIDED (a): unpaged; prune bounds the
   set. Made explicit in REQ-4.1.
5. **[Ambiguity, REQ-2.1] role codes: all assigned vs Active-only** — DECIDED (a):
   all assigned incl. Inactive roles (assignment truth; effect lives in REQ-6).
   Amended REQ-2.1.
6. **[Ambiguity, REQ-3.3/5.5] audit on idempotent no-op calls** — DECIDED (a): audit
   every accepted call, mirroring suspend. Amended REQ-3.2/5.2.
7. **[Inconsistency, REQ-5.2 vs 3.2] audit atomicity wording weaker on revoke** —
   DECIDED (a): same-transaction required on both. Amended REQ-5.2.
8. **[Determinism, REQ-4.1] equal `issuedAt` gives unstable order** — DECIDED (a):
   session id tiebreak. Amended REQ-4.1.
9. **[Unstated assumption, REQ-7.2 × REQ-1/2/6] any `user.view` holder (even Scoped
   tier) sees the full cross-tenant admin directory** — DECIDED (a): intended; admin
   accounts are control-plane data, permission axis is the control (`IAdminQuery`
   seam governs tenant business reads only). No text change.
10. **[Conflict with SFS convention, REQ-1.5] draft required 400 for non-whitelisted
   filter/sort fields, but the mandatory SFS convention (search-filter-sort.md
   REQ-3.3/3.4, exemplar `AdminRoleSfs.ApplyFilters`) silently DROPS unknown
   fields/operators and 400s only malformed JSON and invalid values** — found during
   grill codebase pass 2026-07-05, DECIDED: align with convention. Amended REQ-1.5,
   added REQ-1.8.
11. **[Security semantic, REQ-3.5] should sessions live at suspension time resume
   after reactivate?** — grill question 2026-07-05, DECIDED: NO — reactivate revokes
   all target sessions before activating (fresh-login guarantee; blocks
   stolen-cookie resumption). Idempotent already-Active call revokes nothing.
   Replaced REQ-3.5, added REQ-3.6. Uses existing `RevokeAllForAdminAsync`; suspend
   flow untouched.

### Codex adversarial review findings — 2026-07-05 (Act 2, round 1)

12. **[404 correctness, REQ-6.3] `ListEffectivePermissionsAsync` returns an empty
   set for a nonexistent admin id (no existence check in the repo method)** — the
   handler MUST resolve the account first and 404 on unknown, never infer "no
   permissions" from an empty set. REQ-6.3 already mandates the 404; captured as a
   design/handler constraint. Same pattern applies to the sessions list (REQ-4.4).

---

## Increment 2 — Org profile fields & master data (2026-07-06)

> Additive slice on top of the shipped six endpoints. Unlike Increment 1, this
> increment DOES add tables + a migration (see REQ-10.1 — it supersedes the
> Increment-1 "no new tables/migration" scope, REQ-7.4). Motivation: an admin
> account must record its ตำแหน่ง / สถานที่ปฏิบัติงาน / ระดับ / ฝ่าย-ภาค, and each is a
> RELATION to a managed master list, not free text — so the values stay controlled,
> renameable, and referable.

## REQ-8: Org-profile fields on an admin account (FK to master lists)

**User Story:** As a platform operator, I want each admin account to carry its
position, office, level, and division as references to managed lists, so the org
directory is consistent and the values are controlled.

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL let each `AdminAccount` carry four OPTIONAL org-profile references — `position` (ตำแหน่ง), `office` (สถานที่ปฏิบัติงาน), `level` (ระดับ), `division` (ฝ่าย/ภาค) — each a nullable FK to its own master table (`Positions`/`Offices`/`Levels`/`Divisions`). NULL means "not set" (an invited account has no known profile yet).
- 8.2 WHEN `POST /api/v1/admins` is called THE SYSTEM SHALL accept optional `positionId`/`officeId`/`levelId`/`divisionId`; each supplied id MUST reference an existing, ACTIVE master, else 400 ProblemDetails.
- 8.3 WHEN `PUT /api/v1/admins/{id}/profile` is called for an existing account THE
  SYSTEM SHALL replace ALL four references (a null field clears that dimension) and
  respond 204; IF the id is unknown THEN 404; IF a supplied master id does not
  reference an existing ACTIVE master THEN 400.
- 8.4 WHEN a `PUT .../profile` call is accepted THE SYSTEM SHALL append an append-only `AdminAccountAudit` entry (`update-profile` action, acting admin id, correlation id, target admin id) in the same keyed `"admin"` transaction as the update.
- 8.5 WHEN `GET /api/v1/admins/{id}` is called THE SYSTEM SHALL include, per dimension, either `null` (unset) or a resolved `{id, code, name}` reference.
- 8.6 THE SYSTEM SHALL gate `PUT .../profile` on the existing `user.manage` permission (fail-closed → 403); `POST /admins` keeps its existing `Super` gate, so supplying profile ids at invite is Super-only.

## REQ-9: Master data CRUD (Position / Office / Level / Division)

**User Story:** As a platform operator holding `user.manage`, I want to manage the
four profile master lists at runtime, so new positions/offices/levels/divisions do
not require a code change or migration.

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL expose, per dimension, `GET` (list) / `POST` (create) / `PUT /{id:guid}` (update) under `/api/v1/admins/master-data/{positions|offices|levels|divisions}`. *(Superseded — the `master-data` wrapper and `/admins` parent are both gone: hierarchical-naming dropped the wrapper (`/api/v1/admins/{segment}`), masterdata-split's REQ-6.1 (amended 2026-07-20) moved the dimensions out from under `/admins` entirely to their own standalone areas `/api/v1/{positions|offices|levels|divisions}`, and the same amendment added `GET /{id:guid}` + `DELETE /{id:guid}` per dimension.)*
- 9.2 WHEN the list endpoint is called THE SYSTEM SHALL return a paged result (`page`/`limit`), an optional escaped substring `search` over code + name, ordered by name, each row `{id, code, name, isActive}`.
- 9.3 WHEN create is called THE SYSTEM SHALL require `code` (immutable identity, `^[a-z0-9_]+$`) + `name`; a duplicate code within that dimension → 409; a malformed code → 400.
- 9.4 WHEN `PUT /{id}` is called THE SYSTEM SHALL rename `name` and toggle `isActive` (the code is immutable, taken from the route); unknown id → 404.
- 9.5 THE SYSTEM SHALL soft-deactivate via `isActive` and SHALL NOT hard-delete a master (the `AdminAccount` FK is `Restrict`); an inactive master cannot be newly assigned (REQ-8.2/8.3) but existing references remain valid.
- 9.6 THE SYSTEM SHALL gate every master-data endpoint on `user.manage` (fail-closed → 403) with the admin session policy + CSRF on unsafe methods.

## REQ-10: Increment-2 authorization, wire, and persistence guarantees

**Acceptance Criteria (EARS):**

- 10.1 THE SYSTEM SHALL add four control-plane tables (`Positions`/`Offices`/`Levels`/`Divisions`, schema `producer`, table-per-concrete-type — no base table, no discriminator) plus ONE migration, and SHALL grant `pol_admin` `SELECT, INSERT, UPDATE` on each (no DELETE — soft-deactivate only); this supersedes the Increment-1 zero-migration scope (REQ-7.4).
- 10.2 THE SYSTEM SHALL wire the existing `user.manage` catalog key (previously unused by any endpoint) as the write gate — no new permission key.
- 10.3 THE SYSTEM SHALL leave every Increment-1 endpoint's route and behavior unchanged; `POST /admins` is extended ONLY additively (optional nullable fields).
- 10.4 THE SYSTEM SHALL serve master-data CRUD through a generic store that bypasses Mediator (simple reference data) but STILL commits through the keyed `"admin"` `IUnitOfWork`; master mutations are NOT audited (lower-stakes reference data), while the admin profile edit IS audited (REQ-8.4).
13. **[Atomicity, REQ-3.2/5.2] session revoke uses `ExecuteUpdateAsync` (immediate,
   change-tracker-bypassing)** — atomic with the audit insert ONLY if both run
   inside one `ExecuteInTransactionAsync` on the shared keyed `"admin"` context
   (confirmed `AdminHostWiring.cs:177-188`). Enforced as: reactivate + session
   revoke are Mediator commands, host composes nothing; covered by an atomic-rollback
   integration test.
14. **[Determinism, REQ-1.3] id tiebreak was specified only for the default sort** —
   generalized to EVERY sort ordering. Amended REQ-1.3.
15. **[Audit granularity, REQ-5.2] `AdminAccountAudit` has no session/family column;
   it cannot record WHICH session was revoked** — accepted without migration
   (zero-migration scope): the audit row records who/whom/when via
   action+actorId+targetAdminId+correlationId; the specific session/family id is
   emitted on the structured security log line sharing that correlationId. No REQ
   change (REQ-5.2 already scopes the audit fields); logged so it is not read as a
   gap.
16. **[Route binding] list at the `/admins` group root would render the forbidden
   trailing slash** — list maps on `api.MapGet("/admins")` (mirrors the existing
   `POST /admins`); all id-routes carry `:guid`. Design/host constraint; covered by
   a host route test. No REQ change (REQ-7.6 already requires convention conformance).
