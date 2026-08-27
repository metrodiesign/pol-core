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
     REQ-1.

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
     REQ-2, REQ-6. Depends on: 1.

- [x] 3. Reactivate (lifecycle write) — `POST /api/v1/admins/{id:guid}/reactivate`,
     Super-gated. New `AdminAccount.Reactivate()` domain method (idempotent) +
     `AdminAuditAction.Reactivate`; `ReactivateAdminCommand`/handler that, inside ONE
     keyed `"admin" IUnitOfWork.ExecuteInTransactionAsync` (load account in the
     lambda), computes `wasSuspended`, calls `Reactivate()`, revokes ALL target
     sessions via `RevokeAllForAdminAsync` ONLY when `wasSuspended` (REQ-3.5/3.6),
     appends the audit on every accepted call, `SaveChangesAsync`; 204; 404 unknown;
     idempotent already-Active. Done = suspend→reactivate re-activates and revokes
     sessions atomically; already-Active is a no-revoke 204.
     REQ-3. Depends on: 1.

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
     REQ-4, REQ-5. Depends on: 1.

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
     REQ-7. Depends on: 1, 2, 3, 4.

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
     REQ-8, REQ-9, REQ-10. Depends on: 1, 2.

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
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
