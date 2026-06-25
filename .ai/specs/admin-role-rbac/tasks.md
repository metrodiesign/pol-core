# Implementation Tasks: Admin Role RBAC

> Status: approved 2026-06-24 (AFK-delegated — autonomous completion authorized)

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.
> Design resolutions B1–B4 / S1–S7 (spec-architect critique) are binding — see design.md.

- [x] 1. Persistence foundation: catalog + role tables, migration, seed, grants — create the 5 control-plane tables in schema `producer` (`AdminPermissionGroups`, `AdminPermissions`, `AdminRoles`, `AdminRolePermission` join, `AdminRoleAssignments`) + EF configs (mirror `AdminConfigurations.cs`), add nullable `TargetRoleId` to `AdminAccountAudits` (entity + factory `For(...)` + config), one migration `AddAdminRoleRbacTables` that creates tables, GRANTs per-table to `pol_admin` (catalog SELECT; role tables SELECT,INSERT,UPDATE,DELETE; audits stay SELECT,INSERT), and seeds 5 groups + 14 permissions + 5 roles + their permission grants (super_admin stable Guid). No-`Utc` datetime names (N3).
     Satisfies: REQ-1, REQ-12, REQ-10.2 (column). Verify: integration — migration applies on SQL :11434, seed rows present (5/14/5), FK integrity, `pol_admin` CRUD on role tables.
     Evidence: `dotnet ef database update` applied `20260624154034_AddAdminRoleRbacTables` on live SQL Server 2025 (:11434, .env.integration); Integration.Tests `AdminRoleRbacGrantsTests` green — seed = 5 groups / 14 permissions / 5 roles, super_admin holds 14 grants, auditor Inactive; pol_admin SELECT-only on catalog + full CRUD on role tables; pol_app denied; FK rejects a non-catalog grant. viewports: n/a (backend, no UI). deviations: catalog GRANT tightened to SELECT-only (design said SELECT,INSERT) — least privilege; the catalog is only ever written by migrations, never at runtime.

- [x] 2. Domain: `AdminRole` aggregate + `AdminRoleStatus` + `AdminRoleAssignment` + `AdminRolePermission` + unit tests — `AdminRole : AggregateRoot<Guid>`: private ctor, `Create(...)`, `Code` immutable/trimmed/non-empty/≤64, `Description`/`Color` nullable, `SetPermissions` rejects keys outside the catalog, `Deactivate` throws for the `super_admin` seed. `AdminRoleStatus {Active,Inactive}`. `AdminRoleAssignment` mirrors `AdminTenantAssignment`. Co-located unit tests.
     Satisfies: REQ-2, REQ-3.1, REQ-3.3, REQ-5 (pure union logic), REQ-8.3. Verify: `dotnet test` Admin domain unit tests green.
     Evidence: Admin.Tests 53 passed incl new `AdminRoleTests` (trim/dedup subset, reject unknown key, blank/over-64 code rejected, super_admin not deactivatable while others are, union excludes Inactive roles, catalog shape 14/5) + `SelfProvision_binds_the_seed_super_admin_role`. Command: `dotnet test pol-core.slnx --filter "Category!=Integration"`. viewports: n/a (backend). deviations: none.

- [x] 3. Application: `IAdminRoleRepository` + CQRS handlers + delete-guard — repo on the keyed `"admin"` `ProducerDbContext` (`ListEffectivePermissionsAsync` LINQ union; `AssignmentExistsAsync`; no `SaveChangesAsync` — commit via keyed `IUnitOfWork`). CQRS Create/Update(code immutable)/Delete(409 if users)/SetAdminRoles/ListRoles/GetRole/ListPermissions; unknown key → 400, duplicate code → 409.
     Satisfies: REQ-2.2, REQ-2.3, REQ-2.4, REQ-3.2, REQ-4. Depends on: 1, 2. Verify: build green; CRUD/409/idempotent set covered.
     Evidence: build 0 errors/0 warnings (40 projects); handlers commit via `[FromKeyedServices("admin")] IUnitOfWork.ExecuteInTransactionAsync` mirroring `AssignTenantHandler` (unique-violation → ConflictException → 409 backstop); `DeleteRole` 409 via `CountAssignmentsForRoleAsync>0`; `SetAdminRoles` resolves codes→ids (unknown → 400) and diffs add/remove (idempotent). FK + grant behavior proven green in Integration.Tests (task 1). viewports: n/a (backend). deviations: `AdminRolePermission` mapped as an EF child navigation (`HasMany`) of the `AdminRole` aggregate (still a standalone table + composite-unique + FK to catalog) rather than a repo-managed sibling (S3).

- [x] 4. Resolution: effective permissions into `AdminResolution` — `Permissions` added as a non-positional `init` property (empty default) so the four existing positional `new AdminResolution(...)` sites compile unchanged (B1); populated via `with { Permissions = … }` in `ResolveAdminHandler` (by subject) and `ResolveAdminByIdHandler` (per-request). Inactive-role exclusion + zero-role empty.
     Satisfies: REQ-5. Depends on: 3. Verify: build green; union test green.
     Evidence: build 0/0; Admin.Tests `Effective_permissions_are_the_union_over_active_roles_only` green; the per-request gate reads `IAdminScope.Current.Permissions` bound from `ResolveAdminByIdHandler`. viewports: n/a (backend). deviations: none.

- [x] 5. Enforcement + endpoints + parity guard — `RequirePermission(key)` filter (fail-closed 403, reads `IAdminScope`, registers `RequiredAdminPermission` metadata); inline boot parity guard `AdminPermissionParity.Assert` over `EndpointDataSource` vs code-canonical `AdminPermissions.AllKeys` (no boot DB). Register `IAdminRoleRepository`. Endpoints `GET /admin/permissions|roles|roles/{code}`, `POST/PUT/DELETE /admin/roles`, `PUT /admin/admins/{id}/roles` (status projected to `"active"/"inactive"`); `/admin/me` + `permissions`. Tier path untouched.
     Satisfies: REQ-1.5, REQ-6, REQ-7, REQ-9, REQ-11. Depends on: 3, 4. Verify: host tests 403/200, gating, parity.
     Evidence: Hosts.Tests 115 passed incl `AdminPermissionAuthorizationTests` (admit / deny-missing / fail-closed-unbound) and `AdminPermissionParityTests` (catalog keys pass, bogus key flagged); Architecture.Tests 43 green (Admin module still isolated, tier path untouched → REQ-7.3). viewports: n/a (backend). deviations: parity guard is an inline boot assertion against the code-canonical key set instead of a `BackgroundService`/boot-DB query (B3) — fail-fast, no DB dependency; a sync test keeps DB seed == code catalog.

- [x] 6. Bootstrap role assignment + role-event audit — `SelfProvisionSuperAdminHandler` assigns the seed `super_admin` role idempotently (guarded by `AssignmentExistsAsync`) inside the bootstrap transaction; migration back-fills existing Super accounts; role CRUD/assign/unassign append `AdminAccountAudit` rows (`TargetRoleId` / `TargetAdminId`).
     Satisfies: REQ-8.1, REQ-8.2, REQ-10. Depends on: 1, 2, 3. Verify: bootstrap binds exactly one super_admin + audits.
     Evidence: Admin.Tests `SelfProvision_binds_the_seed_super_admin_role_and_audits_it` green (single assignment + `role-assigned` audit with `TargetRoleId`); existing race-idempotency test still green; migration back-fills Tier=1 accounts via idempotent `NOT EXISTS`. viewports: n/a (backend). deviations: none.

- [x] 7. Cross-cutting tests: integration + host + architecture — grants/seed/FK on live SQL; permission gate + parity host tests; Architecture.Tests pass.
     Satisfies: REQ-1.4, REQ-6 (host), REQ-7.3, REQ-11 (host). Depends on: 5, 6. Verify: full `dotnet test` green.
     Evidence: non-integration suite green — Admin 53, Hosts 115, Architecture 43, all modules pass (`dotnet test --filter "Category!=Integration"`); Integration 42 passed on live SQL Server 2025 (`.env.integration`, :11434) via the repo runbook (docker pol-sql → bootstrap principals → `ef database update` → `dotnet test --filter "Category=Integration"`). `scripts/spec-trace.sh admin-role-rbac` → OK, 38 criteria referenced, EARS lint pass. viewports: n/a (backend). deviations: run locally against the throwaway pol-sql container; CI runs the identical Category=Integration suite.

## Suggested execution batches

> COUPLED feature (all tasks share the role tables, `AdminRole` type, and catalog
> primitives) → run ALL tasks in ONE session (`/spec-implement all`). Order is
> foundational-first (1 → 2 → 3 → 4 → 5/6 → 7).
