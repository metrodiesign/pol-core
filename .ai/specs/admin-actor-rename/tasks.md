# Implementation Tasks: Admin Identity Foundation + RBAC (slice of admin-actor-rename)

> Status: implemented 2026-06-23 (unit-verified; integration + migration round-trip need live SQL Server / CI).
> Branch `feat/admin-identity-rbac`. Scope = admin-foundation slice only (REQ-3..10, REQ-11 additive, REQ-12
> admin-parts, REQ-13 new). Producer rename (REQ-1/2 + rename-half of REQ-11) DEFERRED — see scope banner in
> requirements.md. **Module decision:** admin code lives in a NEW `Admin` module (Admin.Domain/Application/
> Infrastructure), separate from `Identity` (producer-side) — control plane != data plane.

- [x] 1. Admin.Domain — admin aggregates
     `AdminTier`/`AdminStatus` enums; `AdminAccount` (SelfProvision/CreateScoped/BindSubject/Suspend, block
     self-suspend); `AdminTenantAssignment`; `AdminAccountAudit` (append-only, actor tuple) + `AdminAuditAction` consts.
     Satisfies: REQ-3 (model), 8.2 (self-suspend), 10 (audit shape)
     Verify: `dotnet test tests/Admin.Tests`
     Evidence:
       - test: `dotnet test tests/Admin.Tests` -> Passed 29, Failed 0 (domain + handlers)
       - deviations: code in `Admin.Domain` (new module), not `Identity.Domain` (per user direction)

- [x] 2. Admin.Application — ports, resolution, commands
     `AccessibleTenants`; `IAdminScope`; ports `IAdminAccountRepository`/`IAdminAccountAuditWriter`/`IAdminTenantDirectory`;
     `ResolveAdminQuery` (Resolved/Suspended/NotFound; folds accessible resolution); commands
     SelfProvisionSuperAdmin/BindInvitedAdmin/CreateScopedAdmin/AssignTenant/UnassignTenant/SuspendAdmin
     (audit each; assign validates active tenant + uniqueness + Scoped-target).
     Satisfies: REQ-4, 5(logic), 6.1/6.2, 8.1/8.4/8.5(logic), 10.1
     Depends on: 1
     Verify: `dotnet test tests/Admin.Tests`
     Evidence:
       - test: `dotnet test tests/Admin.Tests` -> Passed 29, Failed 0
       - deviations: `IAdminDirectory` (design) folded into `ResolveAdminHandler` (single caller); admin's own
         `IAdminTenantDirectory` port instead of reusing Identity's `ITenantDirectory` (module decoupling)

- [x] 3. Admin.Infrastructure — EF configs + repos
     `AdminAccountConfiguration` (filtered unique Subject `[Subject] IS NOT NULL`, unique Email),
     `AdminTenantAssignmentConfiguration` (unique (AdminAccountId,TenantId)), `AdminAccountAuditConfiguration`;
     `AdminAccountRepository`, `AdminAccountAuditWriter`; `AdminModuleRegistration` marker.
     Satisfies: REQ-3.1/3.2 (mapping), 6.2
     Depends on: 2
     Verify: `dotnet build -warnaserror`
     Evidence:
       - build: `dotnet build pol-core.slnx -warnaserror` -> 44 projects, 0 errors, 0 warnings
       - wired into `ModuleAssemblies.Producer` (HostModuleAssemblies.All) so configs are discovered

- [x] 4. Migration (additive) + control-plane leak test
     `20260623105857_AddAdminIdentityTables` (EF-scaffolded, additive-only): CREATE 3 tables + indexes, NO RLS
     predicate, NO touch of TenantUsers/TenantIsolationPolicy; grants pol_admin (SELECT/INSERT/UPDATE; +DELETE
     on assignments; audits SELECT/INSERT; pol_app NONE); reversible Down with REVOKE + DropTable.
     `IntegrationDb.InsertAdminAccountAsync` added (InsertTenantUserAsync kept). `AdminIsolationIntegrationTests`.
     Satisfies: REQ-11 (additive), 3.2
     Depends on: 3
     Verify: `dotnet ef migrations has-pending-model-changes`; `dotnet test --filter Category=Integration` (live SQL)
     Evidence:
       - drift: `dotnet ef migrations has-pending-model-changes` -> "No changes ... since the last migration"
       - migration applied on live SQL 2025: `dotnet ef database update` -> AddAdminIdentityTables applied;
         producer.AdminAccounts/AdminTenantAssignments/AdminAccountAudits confirmed present
       - integration (RAN on live SQL 2025, isolated container :11434): `AdminIsolationIntegrationTests` -> 6/6
         pass (pol_app cannot read/write admin tables; filtered-unique subject; null-subject invites coexist;
         unique email). Full Integration suite 36/37; the 1 fail = pre-existing OrdersReconciliation test-isolation
         artifact (inserts 555 w/o cleanup -> 1665 after 3 local re-runs on a persistent DB; green on fresh/CI),
         unrelated to admin. Up/Down round-trip = CI.

- [x] 5. Host wiring + endpoints + SPA glue
     `AdminTenantDirectory` (IAdminTenantDirectory impl), `AdminScope` (IAdminScope holder), `IAdminQuery`+`AdminQuery`
     (seam), `AdminResolutionMiddleware` (allowlist self-provision idempotent / invite-bind / suspended->403 /
     admin_tier claim / MFA log), `AdminTierAuthorization.RequireAdminTier`, empty-allowlist boot warning,
     `AddAdminIdentity` registration on pol_admin keyed scope. Endpoints: NEW `GET /admin/me`, `POST /admin/admins`,
     `POST /admin/admins/{id}/tenants`, `DELETE .../{tenantId}`, `POST /admin/admins/{id}/suspend`; MODIFY
     `POST /admin/tenants` (+Super), `GET /admin/tenants/{code}` (via IAdminQuery), approve (accessible check).
     appsettings: AdminAllowlist + admin-console dev CORS origin.
     Satisfies: REQ-4, 5, 6.3, 7.1/7.3, 8, 9, 13
     Depends on: 3
     Verify: `dotnet test tests/Hosts.Tests`
     Evidence:
       - test: `dotnet test tests/Hosts.Tests` -> Passed 65, Failed 0 (incl AdminTierAuthorizationTests x4)
       - deviation: `IAdminScope` lives in Admin.Application (not BuildingBlocks) — it carries AdminResolution;
         `IAdminQuery` + impl in host (returns TenantView; ArchTest bans Identity/Admin->Tenant)

- [x] 6. Architecture.Tests gate + canon + REQ-trace
     `AdminArchitectureTests` (Admin.Domain pure; Admin.Application not depend Tenant/Identity; layers not depend
     Host); `AdminSeamArchitectureTests` (REQ-7.2: only `AdminQuery` may send `GetTenantQuery`). Canon:
     CODING_STANDARDS.md (1 schema `producer`; AdminUser->AdminAccount + new admin entities; keep TenantUser),
     ARCHITECTURE.md (admin control-plane no-RLS / scoped app-layer-floor exception, 7.4/12.2).
     Satisfies: REQ-7.2, 7.4, 12 (partial)
     Depends on: 5
     Verify: `dotnet test tests/Architecture.Tests` + `dotnet test tests/Hosts.Tests`
     Evidence:
       - test: `dotnet test tests/Architecture.Tests` -> Passed 47, Failed 0 (incl AdminArchitectureTests x4)
       - test: `AdminSeamArchitectureTests` green in Hosts.Tests (Mediator source-gen namespace excluded)

## REQ trace (in-scope)

REQ-3 -> AdminAccount + AdminConfigurations (filtered-unique/email) + InsertAdminAccount leak test ·
REQ-4 -> AssignTenant/UnassignTenant (+ DELETE grant) · REQ-5 -> SelfProvisionSuperAdmin + AdminResolutionMiddleware
allowlist + empty-allowlist boot warning · REQ-6 -> ResolveAdminHandler + IAdminScope (resolve once) ·
REQ-7 -> IAdminQuery seam + AdminSeamArchitectureTests + canon floor note · REQ-8 -> RequireAdminTier + approve
accessible check + self-suspend block · REQ-9 -> MFA amr/acr best-effort log · REQ-10 -> AdminAccountAudit (append-only
grant) · REQ-11 -> additive migration (no policy touch) · REQ-12 -> canon (admin model + floor exception; producer
rename deferred) · REQ-13 -> GET /admin/me

## Deferred (out of this PR — see scope banner)

REQ-1, REQ-2 (producer `TenantUser*`->`ProducerAccount*` rename) + the rename half of REQ-11/REQ-12. Design retained.
