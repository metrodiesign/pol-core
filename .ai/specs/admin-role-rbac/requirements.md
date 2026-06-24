# Requirements: Admin Role RBAC

> Status: approved 2026-06-24 (AFK-delegated — user authorized autonomous completion without per-gate review)

## Overview

The Admin Console (platform operators / องค์กรพนักงาน — NOT producers) today authorizes only by a
two-value `AdminTier` (Super/Scoped) that governs *tenant-reach* (which producers' data an admin may see).
There is no concept of a named role or a granular action permission. This feature adds a second,
**orthogonal** axis — role → permission — that governs *what action* an admin may perform, without
touching the existing tier/tenant-reach axis. Permissions and their display groups live in the database as
an **extensible, feature-sourced catalog** (new API features add permissions via their own migration). A
role is an admin-managed, named subset of the catalog; an admin holds zero or more roles; the admin's
effective permission set is the union over their *active* roles. The frontend (`pol-admin`) already ships
the matching UI as a mock-backed shell; this spec delivers the backend it consumes. This realizes the
"enum → permission catalog + role model" explicitly deferred by the `admin-actor-rename` spec.

## REQ-1: Permission Catalog (DB, extensible, feature-sourced)

**User Story:** As a platform engineer, I want permissions and their display groups stored as data seeded
per feature, so that new API capabilities can add permissions without changing a central enum.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL persist the permission catalog as two control-plane tables, `AdminPermissionGroups`
  (Key, LabelTh, SortOrder) and `AdminPermissions` (Key, GroupKey, LabelTh, SortOrder), in schema
  `producer`.
- 1.2 THE SYSTEM SHALL enforce that every `AdminPermissions.GroupKey` references an existing
  `AdminPermissionGroups.Key` (foreign key).
- 1.3 THE SYSTEM SHALL seed an initial catalog of exactly 5 groups (`txn`, `merchant`, `finance`, `user`,
  `system`) and 14 permissions matching the frontend contract in `pol-admin/src/lib/mock/producer-role.ts`.
- 1.4 WHERE a new feature ships a migration that inserts additional group/permission rows, THE SYSTEM SHALL
  include them in the catalog with no change to existing code.
- 1.5 WHEN `GET /admin/permissions` is called by an authenticated admin, THE SYSTEM SHALL return the groups
  and permissions ordered by `SortOrder`, in a shape the frontend renders
  (`{ groups: [{key,label}], permissions: [{key,label,resource}] }`).

## REQ-2: Admin Role Aggregate & CRUD

**User Story:** As an admin with role-management rights, I want to create and maintain named roles, so that
I can model job functions (finance, support, ...) as reusable permission sets.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL model an `AdminRole` with a unique immutable `Code`, a `Name`, a `Description`, a
  `Color`, a `Status` (Active/Inactive), and a set of granted permission keys.
- 2.2 WHEN a role is created, THE SYSTEM SHALL trim and require a non-empty `Code` (≤64 chars) and `Name`,
  and persist `Status` = Active by default.
- 2.3 IF a role is created with a `Code` that already exists, THEN THE SYSTEM SHALL reject the request with
  409 Conflict and not create a duplicate.
- 2.4 WHEN a role is updated, THE SYSTEM SHALL allow changing Name/Description/Color/Status/permissions but
  SHALL NOT allow changing `Code`.
- 2.5 THE SYSTEM SHALL seed 5 roles (`super_admin` active/14, `ops_admin` active/6, `finance` active/5,
  `support` active/3, `auditor` inactive/3) matching the frontend contract.

## REQ-3: Role → Permission Grants (subset of catalog)

**User Story:** As an admin, I want a role to grant only real permissions, so that a role can never confer a
capability the system does not define.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL store a role's granted permissions as `AdminRolePermissions` (RoleId, PermissionKey)
  with a unique (RoleId, PermissionKey) pair.
- 3.2 THE SYSTEM SHALL enforce that every granted `PermissionKey` references an existing
  `AdminPermissions.Key` (foreign key).
- 3.3 IF a create/update request grants a permission key absent from the catalog, THEN THE SYSTEM SHALL
  reject it with 400 Bad Request before persisting any change.

## REQ-4: Admin ↔ Role Assignment

**User Story:** As an admin, I want to assign roles to admin accounts, so that operators gain the
permissions their job requires.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL store admin-to-role links as `AdminRoleAssignments` (AdminAccountId, RoleId,
  AssignedByAdminId, AssignedAt) with a unique (AdminAccountId, RoleId) pair.
- 4.2 WHEN `PUT /admin/admins/{adminId}/roles` sets an admin's roles, THE SYSTEM SHALL make the persisted
  assignment set exactly equal the request set (add/remove the difference) idempotently.
- 4.3 THE SYSTEM SHALL report a role's bound-user count as the number of its assignments.
- 4.4 IF a role is deleted WHILE it has ≥1 assignment, THEN THE SYSTEM SHALL reject the delete with 409
  Conflict (a role with bound users is undeletable).

## REQ-5: Effective Permission Resolution

**User Story:** As the authorization layer, I want an admin's effective permissions resolved per request, so
that role and grant changes take effect on the next request.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL compute an admin's effective permission set as the union of permission keys over the
  admin's assigned roles whose `Status` = Active.
- 5.2 WHILE a held role is Inactive, THE SYSTEM SHALL exclude that role's permissions from the union.
- 5.3 WHEN an admin request is authenticated, THE SYSTEM SHALL materialize the effective permission set into
  the per-request admin scope (read-only, fresh each request).
- 5.4 THE SYSTEM SHALL treat an admin with zero active roles as having zero permissions.

## REQ-6: Permission Enforcement

**User Story:** As a security owner, I want endpoints gated by specific permissions, so that an admin can
only invoke actions their roles grant.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL provide a `RequirePermission(key)` endpoint gate that admits a request only if the
  resolved effective permission set contains `key`.
- 6.2 IF a gated request lacks the required permission, THEN THE SYSTEM SHALL respond 403 Forbidden and not
  execute the handler.
- 6.3 THE SYSTEM SHALL gate all role/permission mutation endpoints with `user.roles`.
- 6.4 WHERE an endpoint is read-only catalog/role data, THE SYSTEM SHALL require only an authenticated
  admin (no specific permission).

## REQ-7: Orthogonality with Tier (no regression)

**User Story:** As the existing Admin module, I want the new layer to be purely additive, so that
tier/tenant-reach behavior is unchanged.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL leave `AdminTier`, `RequireAdminTier`, accessible-tenant resolution, and the
  `IAdminQuery` floor behaviorally unchanged.
- 7.2 THE SYSTEM SHALL NOT grant any permission implicitly from `AdminTier` (a Super tier does not bypass
  permission checks).
- 7.3 THE SYSTEM SHALL keep the Admin module dependency boundary intact (Architecture.Tests pass).

## REQ-8: Bootstrap Role Assignment

**User Story:** As the first platform operator, I want the bootstrap Super admin to be usable immediately,
so that I can manage roles without a chicken-and-egg lockout.

**Acceptance Criteria (EARS):**
- 8.1 WHEN an allowlisted subject self-provisions as the bootstrap Super admin, THE SYSTEM SHALL also assign
  the seed `super_admin` role to that account in the same operation.
- 8.2 THE SYSTEM SHALL make the `super_admin` seed role reference a stable, known identifier so bootstrap can
  assign it deterministically.
- 8.3 IF an admin attempts to deactivate the `super_admin` seed role, THEN THE SYSTEM SHALL reject it (the
  role is the recovery anchor against total lockout).

## REQ-9: Identity Endpoint Exposes Permissions

**User Story:** As the admin SPA, I want my effective permissions from `/admin/me`, so that I can gate UI
affordances client-side.

**Acceptance Criteria (EARS):**
- 9.1 WHEN `GET /admin/me` is called, THE SYSTEM SHALL include a `permissions` array of the caller's
  effective permission keys, additively (existing `adminId`/`email`/`tier`/`accessibleTenants` unchanged).

## REQ-10: Audit of Role Events

**User Story:** As a compliance reviewer, I want role and assignment changes audited, so that authority
changes are traceable.

**Acceptance Criteria (EARS):**
- 10.1 WHEN a role is created, updated, deleted, assigned, or unassigned, THE SYSTEM SHALL append an
  append-only `AdminAccountAudit` row recording the actor, action, and target.
- 10.2 THE SYSTEM SHALL record the target role on role-CRUD audits via a nullable `TargetRoleId` column and
  the target admin on assignment audits via the existing `TargetAdminId`.

## REQ-11: Catalog/Enforcement Parity (no orphan gates)

**User Story:** As an operator, I want every gate to reference a real permission, so that a typo can never
silently lock an endpoint behind an undefined permission.

**Acceptance Criteria (EARS):**
- 11.1 WHEN the API starts, THE SYSTEM SHALL assert that every permission key referenced by a
  `RequirePermission` gate exists in the `AdminPermissions` catalog.
- 11.2 IF a referenced key is absent from the catalog, THEN THE SYSTEM SHALL fail fast at startup with a
  diagnostic naming the offending key.

## REQ-12: Control-Plane Persistence

**User Story:** As the data-protection owner, I want the role tables on the control plane, so that they
follow the admin-table security model.

**Acceptance Criteria (EARS):**
- 12.1 THE SYSTEM SHALL create all role/catalog tables in schema `producer` WITHOUT a tenant RLS predicate
  (control-plane), granting access to `pol_admin` only.
- 12.2 THE SYSTEM SHALL keep audit tables append-only (SELECT, INSERT for `pol_admin`).

## Edge Cases & Open Questions

- **Self-lockout beyond super_admin:** v1 guards only the `super_admin` seed role against deactivation
  (REQ-8.3) and relies on the natural "undeletable while assigned" rule (REQ-4.4). It does NOT prevent an
  admin from removing `user.roles` from their own remaining role; recovery in that rare case is via a DB/
  migration re-seed, mirroring the existing bootstrap recovery path. Accepted limitation for v1.
- **Color values:** stored as a free string (≤16) and validated only for length; the frontend constrains
  the input to its palette. Not enforced server-side.
- **Permission/group authoring:** dev/feature-sourced (migration/seed) only; no admin-facing CRUD for the
  catalog itself in v1 (permissions must bind to real enforcement points).
- **Frontend wiring** (`pol-admin` replacing its mock with live calls) is a separate slice in another repo.
