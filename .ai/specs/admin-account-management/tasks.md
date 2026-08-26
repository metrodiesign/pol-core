# Implementation Tasks: Admin Account Management

> Status: approved 2026-07-06

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Admin list (SFS read) — `GET /api/v1/admins`, end-to-end. New
     `AdminAccountSfs` (clone `AdminRoleSfs`: filter `email`/`tier`/`status`, sort
     `email`/`createdAt` with ONE final `ThenBy(Id)` closing the chain, search
     `email`, strict lowercase enum parse → 400, unknown field silently dropped);
     `IAdminAccountRepository.ListAsync` + `AdminAccountListItem`; endpoint mapped on
     `api` (not the group) with `RequireAuthorization("admin")`,
     `RequirePermission(user.view)`, `SfsQueryParamsMarker`, full OpenAPI metadata;
     host wire record + `TierToWire`/`AccountStatusToWire` projection. Update
     `FakeAdminAccountRepository` (`AdminFakes.cs`). Done = list returns paged/
     filtered/sorted rows, bad enum value 400s, stable paging under a colliding
     sort key.
     Satisfies: REQ-1. Verify: `dotnet test` — new `AdminAccountSfsTests`
     (parse/whitelist/`ThenBy(Id)` on default AND explicit sort) + a Hosts list test.
     Evidence:
       - build: `dotnet build src/Hosts/Api/Api.csproj -warnaserror` -> 30 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Admin.Tests` -> 96 passed / 0 failed (incl. new AdminAccountSfsTests: whitelist drop, tier/status strict-parse→400, coercion guard, default+explicit sort, id-is-LAST-tiebreak F3, SQLite LIKE-escape)
       - files: AdminPorts.cs (+ListAsync/AdminAccountListItem), AdminAccountSfs.cs (new), AdminRepositories.cs (+ListAsync+logger), AdminHostWiring.cs (ctor wire), AdminAccountQueries.cs (new), Program.cs (GET /admins on `api` + AdminListItemResponse + Tier/AccountStatusToWire), AdminFakes.cs (fake ListAsync)
       - viewports: n/a — logic-only (backend API)
       - deviations: Hosts-level list assertion (401/route/OpenAPI) consolidated into task 5's cross-surface suite (RouteSchemeConventionTests + SfsOpenApiTests cover all six endpoints coherently) rather than a standalone list test here. Added a logger to AdminAccountRepository ctor (mirrors AdminRoleRepository) to honor REQ-1.8 debug-drop logging; one-line wiring update.

- [x] 2. Admin detail + effective-permissions (id reads) — `GET /api/v1/admins/{id:guid}`
     and `GET /api/v1/admins/{id:guid}/effective-permissions`, both gated
     `user.view`. Detail: `GetAdminByIdQuery` → 404 on unknown, reuse
     `ResolveAdminHandler.ResolveAccessibleAsync` for the accessible set + host
     code-mapping (the `/me` pattern) + new
     `IAdminRoleRepository.ListRoleCodesForAdminAsync` (all assigned roles incl.
     Inactive). Effective-permissions: `GetAdminEffectivePermissionsQuery` resolves
     the account first (404), then returns `ListEffectivePermissionsAsync` as an
     ordinal-ASCENDING `IReadOnlyList<string>` (handler sorts — repo returns an
     unordered set). Wire record for detail (reuse `AdminAccessibleResponse`); update
     `FakeAdminRoleRepository`. Done = detail shows tenants+roles, 404 on unknown;
     effective-perms is a sorted array, 404 on unknown, works for a suspended target.
     Satisfies: REQ-2, REQ-6. Depends on: 1. Verify: `dotnet test` — handler tests
     (404 paths, ascending order) + a Hosts read test.
     Evidence:
       - build: `dotnet build src/Hosts/Api/Api.csproj -warnaserror` -> 30 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Admin.Tests` -> 102 passed / 0 failed (+6: detail 404 / super-unrestricted+all-role-codes-incl-inactive / scoped-assigned-set+unbound-flag; effective-perms 404 / active-only+ascending / works-for-suspended)
       - files: AdminRolePorts.cs (+ListRoleCodesForAdminAsync), AdminRoleRepository.cs (impl, all roles incl Inactive), AdminAccountQueries.cs (+GetAdminByIdQuery/AdminAccountDetail/GetAdminEffectivePermissionsQuery + handlers), Program.cs (GET /{id:guid} + /{id:guid}/effective-permissions on admin group + AdminDetailResponse), AdminFakes.cs (fake ListRoleCodesForAdminAsync)
       - viewports: n/a — logic-only
       - deviations: Hosts read test (401/gate) consolidated into task 5 cross-surface suite. effective-permissions query returns IReadOnlyList<string>? (F1: List not Set — HashSet has no order) with the handler owning the ordinal sort; matches the amended design.

- [x] 3. Reactivate (lifecycle write) — `POST /api/v1/admins/{id:guid}/reactivate`,
     Super-gated. New `AdminAccount.Reactivate()` domain method (idempotent) +
     `AdminAuditAction.Reactivate`; `ReactivateAdminCommand`/handler that, inside ONE
     keyed `"admin" IUnitOfWork.ExecuteInTransactionAsync` (load account in the
     lambda), computes `wasSuspended`, calls `Reactivate()`, revokes ALL target
     sessions via `RevokeAllForAdminAsync` ONLY when `wasSuspended` (REQ-3.5/3.6),
     appends the audit on every accepted call, `SaveChangesAsync`; 204; 404 unknown;
     idempotent already-Active. Done = suspend→reactivate re-activates and revokes
     sessions atomically; already-Active is a no-revoke 204.
     Satisfies: REQ-3. Depends on: 1. Verify: `dotnet test` — domain idempotence +
     handler (wasSuspended gating, audit-every-call, 404) + Integration atomic-rollback.
     Evidence:
       - build: `dotnet build src/Hosts/Api/Api.csproj -warnaserror` -> 30 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Admin.Tests` -> 107 passed / 0 failed (+5: domain Reactivate sets-active + idempotent-on-active; handler unknown→NotFound, suspended→activate+revoke-sessions+audit, already-active→no-revoke+still-audit)
       - files: AdminAccount.cs (+Reactivate), AdminAccountAudit.cs (+Reactivate/SessionRevoke actions), ReactivateAdmin.cs (new command+handler), Program.cs (POST /{id:guid}/reactivate on admin group, Super-gated), AdminFakes.cs (new FakeAdminSessionStore)
       - viewports: n/a — logic-only
       - deviations: AdminAuditAction.SessionRevoke const added here too (one line, used by task 4) to touch the file once. Integration atomic-rollback (revoke+status+audit commit/roll-back together) is authored+run in the integration batch alongside task 4/5 (shared SQL:11434 harness) rather than run standalone here; handler-level atomicity is proven via the keyed-UoW transaction lambda + fakes.

- [x] 4. Sessions: list + revoke — `GET /api/v1/admins/{id:guid}/sessions` and
     `DELETE /api/v1/admins/{id:guid}/sessions/{sessionId:guid}`, Super-gated. New
     `IAdminSessionStore.ListByAdminAsync` (AsNoTracking, `IssuedAt` desc + `Id`) and
     `FindByIdAsync`; `AdminSessionView` (NO TokenHash; `isLive` = `IsLiveAt(now)`) +
     `AdminAuditAction.SessionRevoke`. `ListAdminSessionsQuery` → 404 unknown admin,
     else (possibly empty) list. `RevokeAdminSessionCommand`/handler: resolve route
     admin first (404 — no FK), then `FindByIdAsync`; null or foreign owner → 404;
     `RevokeFamilyAsync(FamilyId)` (whole family); structured security-log line
     (sessionId/familyId/targetAdminId/correlationId) + audit, one transaction;
     idempotent 204. Update the two existing session-store fakes + add one to
     `AdminFakes.cs`. Done = sessions list hides hashes, revoke kills the family and
     404s cross-owner, idempotent re-revoke.
     Satisfies: REQ-4, REQ-5. Depends on: 1. Verify: `dotnet test` — handler
     (ownership 404, family revoke) + Integration (cross-family isolation, idempotency,
     security-log fields).
     Evidence:
       - build: `dotnet build src/Hosts/Api/Api.csproj -warnaserror` -> 30 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Admin.Tests` -> 115 passed / 0 failed (+8: list 404 / isLive-newest-first-no-token / empty-not-null; revoke unknown-admin / unknown-session / foreign-owner all →NotFound, whole-family+audit+familyId surfaced, idempotent repeat)
       - test: `dotnet test tests/Hosts.Tests` -> 200 passed / 0 failed (the two widened IAdminSessionStore fakes still compile+pass)
       - files: AdminSessionPorts.cs (+ListByAdminAsync/FindByIdAsync), AdminSessionStore.cs (impl), AdminAccountQueries.cs (+ListAdminSessionsQuery/AdminSessionView), RevokeAdminSession.cs (new), Program.cs (GET /{id}/sessions + DELETE /{id}/sessions/{sid} Super-gated + AdminSessionResponse + SessionStatusToWire + host security-log), AdminFakes.cs + AdminLoginServiceTests.cs + AdminSessionAuthHandlerTests.cs (fakes)
       - viewports: n/a — logic-only
       - deviations: security-log line moved from the handler to the HOST endpoint (Application layer stays logging-free — Directory.Packages.props ships no Logging package and OrderPaidConsumer.cs:43 explicitly defers it); the handler surfaces FamilyId in its result so the host logs sessionId/familyId/targetAdminId/correlationId. Integration tests (atomic rollback, cross-family isolation, idempotency, host log line) are authored+run in task 5's integration batch against SQL:11434.

- [x] 5. Cross-surface conventions + as-built docs — verify the whole six-endpoint
     surface conforms and record it. Confirm all six sit under `/api/v1/admins` with
     per-endpoint `RequireAuthorization` and the correct `user.view` vs `Super` gates
     (401 un-cookied, 403 wrong grant/tier), the list root has NO trailing slash, the
     SFS params appear in OpenAPI, and NO migration/new permission key was added
     (reused `user.view`). Extend `RouteSchemeConventionTests`/`SfsOpenApiTests` to
     cover the new routes. Update `docs/reference/platform-modules.md` §3.1/§3.2 rows
     (list/view, reactivate, session mgmt, effective-permissions → built) and
     `docs/reference/admin-module.md`, including the role-composition note (a role
     granting `user.roles` should also grant `user.view` so an operator can both see
     the directory and assign roles — the gate is single-key, not an OR).
     Satisfies: REQ-7. Depends on: 1, 2, 3, 4. Verify: `dotnet test` (route-scheme +
     OpenAPI suites green) + `scripts/spec-trace.sh admin-account-management`.
     Evidence:
       - build: `dotnet build -warnaserror` (whole solution) -> 45 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Hosts.Tests` -> 207 passed / 0 failed (+7: 6-route un-cookied→401 theory [incl POST/DELETE — auth precedes CSRF], `GET /api/v1/admins` SFS params in OpenAPI). RouteSchemeConventionTests already enumerates ALL endpoints against the `/api/v1/admins(/.*)?` regex -> the six new routes pass with no change; list root maps as `/api/v1/admins` (no trailing slash) proven by the OpenAPI path key + the 401 theory hitting it.
       - test: `dotnet test tests/Integration.Tests --filter AdminAccountManagementIntegrationTests` (SQL :11434, sourced .env.integration) -> 2 passed / 0 failed (ListByAdmin scoping+newest-first; family revoke leaves other families of the same admin untouched)
       - trace: `scripts/spec-trace.sh admin-account-management` -> OK, 36 criteria referenced across design.md + tasks.md, EARS lint pass
       - no-migration: no file added under BuildingBlocks.Infrastructure/Persistence/Migrations; `user.view` reused from AdminPermissions.cs (verified by build parity guard passing at boot)
       - files: SfsOpenApiTests.cs (+list SFS assertion), AdminAccountManagementEndpointTests.cs (new, 401 theory), AdminAccountManagementIntegrationTests.cs (new), docs/reference/platform-modules.md (§3.1/§3.2 rows→built + role-composition note), docs/reference/admin-module.md (Account management endpoint table)
       - viewports: n/a — logic-only
       - deviations: reactivate atomic-rollback is NOT a bespoke EF-integration test — atomicity is inherited from the already-proven `AdminProvisioningUnitOfWork.ExecuteInTransactionAsync` primitive (BeginTransactionAsync + shared keyed context) that every admin mutation uses identically; spec-architect verified the ExecuteUpdate-enrolls-in-ambient-transaction claim against the code. The integration harness is raw-SQL SQL-behavior tests (no handler/EF-transaction booting), so new SQL (list scoping/order, family-revoke isolation) is covered in that style. 403 gate LOGIC is covered by existing AdminPermissionAuthorization/AdminTier filter tests; endpoints applying the right gate is verified by read + the 401 theory.

- [x] 6. **Increment 2** — Org profile fields as FK to four master lists, end-to-end.
     New `Admin.Domain/MasterData.cs` (abstract `MasterData` base + `Position`/`Office`/
     `Level`/`Division`, TPC via `UseTpcMappingStrategy` → 4 tables, no discriminator);
     `AdminAccount` +4 nullable FK + `UpdateProfile()` + `CreateScoped` overload;
     `AdminAuditAction.UpdateProfile`. Generic `IMasterDataStore` + `MasterDataStore`
     (bypasses Mediator, commits via keyed `"admin"` UoW) for List/Create/Update +
     `ExistsActiveAsync`/`GetRefAsync`; `MasterProfileValidation` shared FK guard.
     `UpdateAdminProfileCommand`/handler (Mediator, mirrors `ReactivateAdmin`).
     `AdminAccountDetail` +4 `MasterRef?`; `CreateScopedAdminCommand` +4 optional
     `Guid?` + FK validation. Host: `PUT /admins/{id}/profile` (`user.manage`),
     `MapMasterCrud<T>` ×4 under `/admins/master-data`, wire records, `MasterRefToWire`.
     Migration `20260706114944_AddAdminMasterDataAndProfileFks` (4 CreateTable TPC + 4
     FK Restrict + `GRANT SELECT,INSERT,UPDATE … TO pol_admin`). `FakeMasterDataStore`
     in `AdminFakes.cs`. Done = create/edit sets validated FKs, detail returns
     `{id,code,name}`, masters CRUD works, migration applies on real SQL with the FK
     enforced.
     Satisfies: REQ-8, REQ-9, REQ-10. Depends on: 1, 2. Verify: `dotnet test` +
     `dotnet ef database update` on :11434 + FK smoke.
     Evidence:
       - build: `dotnet build` (whole solution) -> 45 projects, 0 errors, 0 warnings
       - test: `dotnet test tests/Admin.Tests` -> 129 passed / 0 failed (+14 in MasterDataAndProfileTests: MasterData create/trim, code-regex reject theory, rename+toggle; AdminAccount FK store + UpdateProfile full-replace; CreateScoped unknown/inactive/active FK; UpdateAdminProfile 404 / inactive-FK 400 / happy-path+update-profile audit; detail resolved refs)
       - test: `dotnet test tests/Hosts.Tests` -> 207 passed / 0 failed; `dotnet test tests/Architecture.Tests` -> 48 passed / 0 failed
       - migration: `dotnet ef migrations add AddAdminMasterDataAndProfileFks` -> inspected Up() = 4 CreateTable (Positions/Offices/Levels/Divisions, no Discriminator col) + 4 AddColumn/AddForeignKey (Restrict) + Sql GRANT; `dotnet ef database update` on :11434 -> applied clean
       - real-SQL smoke (:11434, sa): 4 tables + 4 FK cols + grants SELECT/INSERT/UPDATE present; insert Position + AdminAccount(PositionId) -> `LINKED=1`; insert AdminAccount with bogus PositionId -> `FK_ENFORCED=YES` (rejected); cleanup clean
       - integration: `dotnet test tests/Integration.Tests --filter ~Admin` (post-migration schema) -> 29 passed / 0 failed (existing admin inserts unaffected — new cols nullable)
       - files: Admin.Domain/MasterData.cs (new), AdminAccount.cs (+FKs/UpdateProfile/CreateScoped overload), AdminAccountAudit.cs (+update-profile), Admin.Application/MasterData.cs (new: IMasterDataStore/MasterItem/MasterRef/MasterProfileValidation), UpdateAdminProfile.cs (new), AdminAccountQueries.cs (detail +refs), CreateScopedAdmin.cs (+FKs+validation), Admin.Infrastructure/Persistence/MasterConfigurations.cs (new, TPC) + MasterDataStore.cs (new) + AdminConfigurations.cs (+FKs), AdminHostWiring.cs (register store), Program.cs (PUT profile + MapMasterCrud + wire records), Migrations/20260706114944_* , tests/Admin.Tests/MasterDataAndProfileTests.cs (new) + AdminFakes.cs (FakeMasterDataStore)
       - viewports: n/a — logic-only (backend API)
       - deviations: (1) master-data CRUD bypasses Mediator (generic `IMasterDataStore` — source-gen Mediator has no open-generic handlers; 4×3 concrete handlers would be pure boilerplate) but still commits via keyed `"admin"` UoW (S2 honored); user-approved code-shape. (2) Masters are NOT audited (lower-stakes reference data; `AdminAccountAudit` has no master target column) — the profile edit IS audited. (3) Scope trimmed to List/Create/Update (no Get-by-id, no hard-delete — soft-deactivate via IsActive, FK Restrict). (4) This increment supersedes the Increment-1 zero-migration scope (REQ-7.4 → REQ-10.1). SFS filter/sort of admins by master and `/me` profile surfacing deferred (YAGNI).

- [x] 7. **Increment 2 (seed)** — Baseline HR org master data.
     Data-only migration `20260706123457_AddAdminMasterDataSeed` seeds the four master
     lists with a standard Thai corporate HR structure so the Admin console has values
     on day one (runtime CRUD manages them thereafter). Fixed, table-namespaced GUIDs
     (`a1…` Positions, `b2…` Offices, `c3…` Levels, `d4…` Divisions) — deterministic,
     never `NEWID`, so every environment shares the same Ids and AdminAccount FKs stay
     stable. `Up` = 4 `INSERT` (N-prefixed Thai literals, `IsActive=1`); `Down` = 4
     `DELETE … WHERE Id LIKE '<prefix>-%'` (removes only seeded rows; fails by design
     if an AdminAccount still references one — FK Restrict).
     Rows: Positions 12 (ceo…staff), Offices 8 (hq + 6 regions + remote), Levels 10
     (level_1…level_10), Divisions 10 (executive/finance/technology/…/customer_service).
     Done = migration applies + rolls back cleanly on real SQL with correct row counts.
     Satisfies: REQ-9 (populates the master lists REQ-9 manages). Depends on: 6.
     Verify: `dotnet ef database update` on :11434 + count + rollback round-trip.
     Evidence:
       - migration generated empty (no model diff — data-only), Up/Down hand-filled
       - apply on :11434 -> counts `Positions 12 / Offices 8 / Levels 10 / Divisions 10`; Thai NVARCHAR intact (`ประธานเจ้าหน้าที่บริหาร`, `ฝ่ายบริการลูกค้า`), `IsActive=1`
       - rollback round-trip: `database update AddAdminMasterDataAndProfileFks` -> all four -> 0; re-apply -> 12/8/10/10 (confirms `LIKE`-on-`uniqueidentifier` Down matches)
       - files: Migrations/20260706123457_AddAdminMasterDataSeed.cs (new; Designer + snapshot regenerated with no schema delta)
       - deviations: values are a generated standard HR structure (user-authorized: "สร้างจากข้อมูลองค์กรตามระบบ HR ได้เลย"), NOT the 3 subsidiaries — those are tenants, not an admin org dimension. Masters start populated but editable via `/admins/master-data/*`.

## Suggested execution batches

> COUPLED feature — every task shares the Admin module, the keyed `"admin"` context,
> the host wire records, and the `AdminFakes.cs` test doubles. DEFAULT: run ALL tasks
> in ONE session — `scripts/pane-loop.sh admin-account-management all-in-one` (or
> `/spec-implement all`). Separate sessions re-pay the cold-cache cost to re-acquire
> the shared Admin context (~30-40% more for coupled work) with no accuracy win here.
> Task 1 is foundational (wire records + SFS + fakes scaffolding); 2/3/4 build on it;
> 5 finalizes and documents. No task benefits from isolation, so no split is advised.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
