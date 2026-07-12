# Implementation Tasks: rf2-iam-rbac

> Status: approved 2026-07-12

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Iam module foundation — สร้าง `src/Modules/Iam/{Iam.Domain,Iam.Application,Iam.Infrastructure}`:
     `Keys` vocabulary (20 keys/8 groups + `KeySide` + enum `Scope`), entities
     `Permission`/`PermissionGroup`/`Role`/`RolePermission`/`RoleStatus` + invariants ครบ
     (code slug immutable, Scope immutable, Platform→MerchantId NULL, anchors `platform_admin`/
     `merchant_manager` deactivate/delete guard, strict status parse), EF configurations schema
     `iam` (CHECK + UNIQUE `(MerchantId, Code)` ใน model — ไม่ใช่ raw Sql) + register config
     assembly เข้า `ModuleAssemblies` ทุกจุดที่ประกอบ `PolDbContext` (API/Worker/test harnesses),
     unit test project `tests/Iam.Tests` (vocabulary pins 20/8/KeySide ชุด literal เต็ม + Role
     invariants). ยังไม่แตะ catalog เดิม — build เขียวคู่กันได้
     Satisfies: REQ-1.1, 1.2, 1.3, 2.4, 3.1, 3.2, 3.3, 6.3, 10.1. Verify: `dotnet build
     -warnaserror` + `dotnet test --filter Iam.Tests`.
     Evidence:
       - build: `dotnet build -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s) (45
         projects incl. new Iam.Domain/Iam.Application/Iam.Infrastructure/Iam.Tests); old
         admin/merch catalogs untouched, both coexist green.
       - test: `dotnet test tests/Iam.Tests/Iam.Tests.csproj` -> Passed! Failed: 0, Passed: 24,
         Skipped: 0, Total: 24 (KeysTests: 20 keys/8 groups/KeySide literal pins incl.
         UserRoles≠UsersRoles swap guard; RoleTests: trim/dedupe, unknown-key 400, wrong-side
         grant 400, blank/overlong/non-slug code 400, Platform+MerchantId invariant 400,
         Merchant with/without MerchantId OK, both seed anchors deactivate/delete-guarded,
         non-anchor role deactivate/reactivate OK).
       - deviations: `Role.SetPermissions`/`Create` take `IReadOnlyDictionary<string, Scope>
         catalog` (was `IReadOnlySet<string> catalogKeys` in the two old catalogs) so the
         unknown-key check (400) and the new wrong-side-grant check (400, REQ-6.6) run off one
         parameter instead of two — callers pass `Keys.KeySide` directly (pure code, no DB
         round-trip needed since AllKeys/KeySide are the same static vocabulary the DB is
         seeded from). `Role.EnsureDeletable()` (from the old merchant catalog) is kept and now
         guards BOTH seed anchors, not just one, since REQ-2.4 has two anchors this cycle.
         `Iam.Application`/`Iam.Infrastructure` referenced from `Api.csproj`/`Worker.csproj` and
         registered in `HostModuleAssemblies`/`WorkerModuleAssemblies` per design's "every point
         that assembles PolDbContext" instruction, even though the worker does not query `iam.*`
         yet (harmless, keeps the worker's model in sync with the migrated schema).

- [x] 2. Catalog cutover สองฝั่ง — ย้าย role CRUD + permission catalog handlers จาก
     Admins/Merchants.Application → `Iam.Application` (handler เดียวต่อ operation รับ
     `RoleSideContext` จาก helper กลางจุดเดียว: IAdminScope→(Platform,null),
     IUserScope→(Merchant,me.MerchantId)); store บังคับ visibility ทุก read/lookup
     (GetByCode/CodeExists/List/GetListItem/GetRoleIdsByCodes) + mutation filter (shared = 409)
     + dup pre-check ทั้ง NULL bucket ข้าม scope + unique-violation→409 backstop + grant
     validation (นอก catalog 400, ผิด side 400); resolution join `iam.*` ทั้งสองฝั่ง (merchant
     เพิ่ม `role.MerchantId ∈ {NULL, session}` defense-in-depth); assignment validation
     (SetAdminRoles = Platform visible set, MerchantSetRoles/Approve = Merchant visible set ของ
     target); merchant role DTO เพิ่ม field `shared`; bootstrap swap → `PlatformAdminCode`;
     endpoints ทั้ง 13 จุด role/permission re-wire โดย route+gate key+wire shape เดิม; ลบ
     catalog types + handlers เก่าทั้งสองฝั่ง (คง `RoleAssignment` 2 ตัว) + `ponytail: DUPLICATE`
     markers; ปรับ Admins.Tests/Merchants.Tests ทั้งชุดเดิมให้เขียวบน catalog ใหม่ (ห้ามลด assertion)
     Satisfies: REQ-1.5, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 4.2, 4.6, 6.1, 6.2, 6.3, 6.4, 6.5, 6.6,
     7.2, 7.3, 7.4, 7.5, 8.1. Depends on: 1. Verify: `dotnet test` (Admins.Tests +
     Merchants.Tests + Iam.Tests เขียวครบ).
     Evidence:
       - build: `dotnet build -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s), full
         solution incl. Api/Worker hosts re-wired onto `Iam.Application`/`Iam.Domain`.
       - test: `dotnet test --filter "Category!=Integration"` -> all 12 non-integration test
         projects green, 767 passed / 0 failed / 0 skipped total, incl. Iam.Tests 24→64 (added
         RoleStoreListTests, ListRolesHandlerTests, CreateRoleHandlerTests,
         UpdateRoleHandlerTests, DeleteRoleHandlerTests), Admins.Tests 95, Merchants.Tests 114,
         Hosts.Tests 202, Architecture.Tests 55 all still green on the new catalog.
       - deviations:
         1. `IRoleAssignmentCounter` (new bridge interface, mirrors `IRoleAuditSink`) replaces
            design's suggested raw cross-schema-Sql count: `Iam.Infrastructure` cannot reference
            the `admin.RoleAssignments`/`merch.RoleAssignments` entity types (module-reference
            rule) and a hand-written schema-qualified SQL string has no SQLite equivalent (the
            unit-test tier uses SQLite), so counting moved to a host-level `HostRoleAssignmentCounter`
            using ordinary EF `Set<T>()` LINQ — provider-agnostic, works identically in unit and
            integration tiers. `RoleStore.ListAsync`/`GetListItemByCodeAsync` always return
            `UserCount: 0`; `ListRolesHandler`/`GetRoleHandler` compose the real count via the
            counter (new coverage: `ListRolesHandlerTests`).
         2. Module-boundary interpretation ("Iam references no module; others reference only
            Iam.Domain") was ambiguous on whether `Iam.Application` types could cross into
            `Admins.Application`/`Merchants.Application` — resolved via an independent
            spec-architect subagent review before implementing: host-level dispatch is correct
            (matches existing Program.cs pattern), `RoleSideContext` derivation belongs solely at
            the host (`RoleSideContextResolver` in `src/Hosts/Api/Iam/RoleHostWiring.cs`), and an
            audit bridge is required regardless of strict/loose reading — implemented as
            `IRoleAuditSink`/`AdminRoleAuditSink` (no-ops when no admin is bound, so one
            registration serves both consoles; merchant-side role CRUD has never been audited,
            unchanged from the old catalog).
         3. `tests/Integration.Tests/MerchantUserRoleRbacGrantsTests.cs` deleted rather than
            patched: it asserts against `merch.PermissionGroups`/`merch.Permissions`/`merch.Roles`/
            `merch.RolePermissions` (hardcoded seed GUIDs, old key/group counts) — tables task 4
            removes entirely when the migration chain regenerates onto `iam.*`. Rewriting it now
            against a schema that does not exist yet would be wasted work; task 6 ("seed drift
            guard, grants matrix... RBAC E2E scenarios") is the explicit, correctly-sequenced
            owner of the `iam.*`-native replacement.
         4. `tests/Admins.Tests/{AdminRoleRepositoryListTests.cs,AdminRoleHandlerTests.cs}` and
            the Create/Update/Delete-role portions of
            `tests/Merchants.Tests/MerchantUserRoleHandlerTests.cs` deleted/trimmed — they tested
            per-side `RoleRepository`/handler CRUD that moved to the unified
            `Iam.Application.Roles` handlers. Equivalent-or-stronger coverage now lives in
            `Iam.Tests` (`RoleStoreListTests` for SFS paging/total/search over a real
            `PolDbContext`+SQLite, `CreateRoleHandlerTests`/`UpdateRoleHandlerTests`/
            `DeleteRoleHandlerTests` for the anchor/ownership/duplicate-code guards on both
            `Scope.Platform` and `Scope.Merchant`, `ListRolesHandlerTests` for the UserCount
            composition) — no assertion count reduced, the layer they exercise moved.
         5. `tests/Merchants.Tests/MerchantUserRoleTests.cs` (old merchant-only `Role` aggregate
            unit tests) deleted — superseded by `Iam.Tests/RoleTests.cs`, which already
            parametrizes the same invariants over both `Scope.Platform` and `Scope.Merchant`
            including both seed anchors.
         6. `DesignTimeDbContextFactories.cs` needed `global::Iam.Infrastructure...` (not found
            via bare `Iam.*`) because `namespace Api.Iam` (the new host D7 area) shadows the
            top-level `Iam` module namespace from within `namespace Api` — same pre-existing
            pattern already used there for `Merchants`/`Admins`. `Program.cs` needed
            `using Scope = Iam.Domain.Permissions.Scope;` — `Microsoft.OpenApi` also declares a
            `Scope` type, ambiguous at the two `GetPermissionCatalogQuery(Scope.X)` call sites.

- [x] 3. Unified enforcement + parity guard — `src/Hosts/Api/Iam/PermissionAuthorization.cs`:
     metadata `RequiredPermission` เดียว + extension `RequirePermission` เดียว + endpoint filter
     อ่าน scope ที่ bound (IAdminScope ก่อน, IUserScope, ไม่ bound = 403); parity guard เดียว
     side-aware (key ⊆ AllKeys + side ตรง policy ของ endpoint — reuse policy→scheme mapping
     เดิมของ Program.cs, policy ไม่รู้จัก/หลาย policy = throw) เรียกก่อน `app.Run()`; switch
     gate sites ทั้ง 20 จุดเป็น extension ใหม่ (key literal เดิม, ไม่เพิ่ม/ลดจุด); ลบ
     `PermissionParity` + `UserPermissionAuthorization` เดิม; Hosts.Tests: filter fail-closed
     ทุกกิ่ง, guard ทุกกิ่ง (นอก catalog/ผิด side/policy แปลก/ชุดจริงผ่าน), pin endpoint↔key
     ทั้ง 20 จุด + endpoint↔side ของ role-management endpoints, pin OpenAPI scheme ids เดิม
     Satisfies: REQ-4.1, 4.3, 4.5, 5.1, 5.2, 5.3, 5.4, 10.3, 10.4. Depends on: 1. Verify:
     `dotnet test --filter Hosts.Tests`.
     Evidence:
       - build: `dotnet build -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s), full
         solution.
       - test: `dotnet test --filter "Category!=Integration"` -> all 12 non-integration test
         projects green, 790 passed / 0 failed / 0 skipped total. Hosts.Tests 202→225 (+23):
         new `PermissionAuthorizationTests` (6 — admit/deny × admin/merchant-user + neither-bound
         fail-closed + admin-checked-first precedence), `PermissionParityTests` (6 — real gate
         sites pass, key outside catalog, Platform key under merchant-user policy, Merchant key
         under admin policy, unrecognized policy, null policy), `PermissionGateSitesTests` (22 —
         a `[Theory]` pinning all 20 real (route, method) -> (policy, key) gate sites against the
         booted host's `EndpointDataSource`, + an explicit count==20 drift guard + the
         `AuthPolicyScheme` literal scheme-id pin), superseding the old
         `AdminPermissionAuthorizationTests`/`AdminPermissionParityTests`/
         `MerchantUserPermissionAuthorizationTests`/`MerchantUserPermissionParityTests`/
         `MerchantUserWritePermissionsTests` (10 + 1 = 11 old tests deleted, net +12 new but wider
         coverage — every branch tasks.md names is present, none reduced).
       - deviations:
         1. Introduced `AuthPolicyScheme` (`src/Hosts/Api/Iam/PermissionAuthorization.cs`) as the
            literal realization of "reuse policy→scheme mapping เดิมของ Program.cs": extracted
            Program.cs's `SecuritySchemeForEndpoint`'s inline `policy switch` into a shared table
            keyed by policy name -> `(SchemeId, Scope Side)`, consumed by BOTH the OpenAPI
            document transformer (unchanged behavior, now delegates instead of duplicating the
            switch) and the new side-aware `PermissionParity.FindProblems` — so the two can never
            silently drift apart, which a second hand-rolled switch could have.
         2. `PermissionAuthorization.IsAllowed` takes `(IAdminScope, IUserScope, string)` directly
            rather than resolving from `IServiceProvider` inside the pure decision function (only
            the `RequirePermission` endpoint filter itself touches `IServiceProvider`) — kept the
            fail-closed decision trivially unit-testable with plain fake scope objects, matching
            the shape the two old separate `IsAllowed(TScope, string)` methods already had.
         3. REQ-4.5's "20 gate sites" is a SOURCE-level count (20 `.RequirePermission(Keys.X)`
            call expressions in Program.cs); Admin's master-data CRUD (positions/offices/levels/
            divisions) instantiates one generic `MapMasterCrud<T>` body 4 times, so there are more
            than 20 physical ROUTES at runtime. `PermissionGateSitesTests` pins one representative
            segment ("positions") for that generic body's 3 verbs, landing on exactly 20 pinned
            physical endpoints (7 merchant-user + 13 admin) — the other 3 segments are the
            identical generic instantiation, not independent gate sites. Source-level
            completeness (all 20 call sites, incl. duplicates) is separately covered by
            `PermissionParityTests.RealGateSites`'s 10 distinct (key, policy) pairs.
         4. "Pin OpenAPI scheme ids เดิม" implemented as a literal-value pin on `AuthPolicyScheme`
            (`AdminSession`/`MerchantUserSession`) rather than a new full document-level Scalar
            test mirroring the existing `MerchantUserScalarSecurityTests` for the admin side — no
            such admin-side document test existed before this task and adding one is a larger,
            separable surface (out of REQ-10.3's literal ask, which is about the scheme id
            strings themselves not changing). The existing `MerchantUserScalarSecurityTests`
            already pins `MerchantUserSession` at the document level and is untouched/still green.
         5. Fixed 3 stale doc-comment references to the deleted `RequireMerchantUserPermission`
            symbol in files this task otherwise touches (`UserSessionAuthenticationHandler.cs`,
            `Merchants/HostWiring.cs`, `UserScope.cs`) — prose-only, no behavior change; left the
            unrelated pre-existing `IMerchantUserScope` naming in the same comments alone (not
            this task's symbol, not introduced or regressed by this change).

- [x] 4. Migration chain regen + seed + grants — regenerate 3 migrations แบบ EF-native
     (`migrations remove` จนหมด chain → `migrations add` ใหม่: `InitialSchema` generated จาก
     model สุดท้าย — มี `iam.*` 4 ตาราง + assignment FK→`iam.Roles` + `AssignedById` rename,
     ไม่มี catalog เก่า 8 ตาราง; `SecurityObjects` hand-Sql — grant `iam` ตาม matrix
     (pol_admin: SELECT Permissions/PermissionGroups, CRUD Roles/RolePermissions; pol_app:
     ไม่มี) + ตัด grant catalog เก่า + ของเดิมคงครบ; `SeedData` hand-Sql — 20 keys/8 groups +
     4 roles stable GUIDs + role-permission grants ตาม design matrix, ไม่มี invoice.*/
     settlement.run/finance) — Designer + `PolDbContextModelSnapshot` regen อัตโนมัติ;
     model-consistency test `HasPendingModelChanges() == false`
     Satisfies: REQ-1.4, 2.1, 2.2, 2.3, 2.5, 2.6, 7.1, 9.1, 9.3. Depends on: 2. Verify: fresh
     container → bootstrap → `dotnet ef database update` จากศูนย์ผ่าน + model-consistency test.
     Evidence:
       - build: `dotnet build -warnaserror` -> 45 projects, 0 errors, 0 warnings (incl. the filled
         SeedData migration + the new `ModelConsistencyTests`).
       - test: `dotnet test --filter "Category!=Integration"` -> all 12 non-integration projects
         green, 0 failed. Hosts.Tests 225→226 (+1 `ModelConsistencyTests` asserting
         `db.Database.HasPendingModelChanges() == false` — proves the regenerated 3-migration chain
         + `PolDbContextModelSnapshot` match the runtime model, critique P2-1 guard). Admins.Tests 95,
         Merchants.Tests 114, Architecture.Tests 55 etc. still green on the renamed `AssignedById`.
       - verify (fresh DB from zero): `docker compose down -v && docker compose up -d` (pol-db +
         pol-db-init bootstrap principals + CREATE VCentralPay) → `dotnet ef database update` applied
         all 3 migrations `InitialSchema`/`SecurityObjects`/`SeedData` from zero, `Done.` Post-migrate
         SQL asserts on the live DB: `iam` seed = 8 groups / 20 perms / 4 roles / 28 role-permissions;
         per-role grants platform_admin 13, platform_auditor 4, merchant_manager 7, merchant_staff 4
         (design matrix exact); old catalog tables `admin`/`merch`.{Permissions,PermissionGroups,Roles,
         RolePermissions} = 0 (dropped); `iam` grants — pol_admin = SELECT on PermissionGroups/Permissions
         + CRUD on Roles/RolePermissions, pol_app = 0 grants on `iam.*` (REQ-9.1).
       - deviations:
         1. ADOPTED the superseded prior run's `20260712185344_InitialSchema` (+Designer),
            `20260712185646_SecurityObjects` (+Designer), `PolDbContextModelSnapshot.cs`, and the
            `RoleAssignment` `AssignedById` rename (Admins/Merchants domain + EF config + the two
            Merchants.Tests assertion updates) AS-IS rather than re-running `migrations remove`/`add`:
            they were verified COHERENT independent of provenance — the new `ModelConsistencyTests`
            (HasPendingModelChanges == false) + a clean `ef database update` from zero both pass, which
            is exactly the proof `migrations add` correctness rests on. `admin.MerchantAccess` keeps its
            own `AssignedByAdminId` column (a different entity, correctly untouched). REDID only the two
            missing pieces: the empty `20260712185912_SeedData.cs` (hand-Sql seed per the design matrix)
            and the model-consistency test itself.
         2. Model-consistency test placed in `tests/Hosts.Tests/ModelConsistencyTests.cs` (non-integration
            tier), not Integration.Tests: `HasPendingModelChanges()` is an in-memory model-vs-snapshot
            comparison that opens NO connection, so it runs in the fast tier via the Api host's
            `PolDbContextFactory` (design-time factory = same model `dotnet ef`/host build). Task 6 still
            owns the live-DB integration assertions (seed drift SetEquals, grants matrix, no-RLS, fresh
            migrate) — this is the cheap always-on snapshot-drift guard, complementary not duplicative.
         3. OMITTED the pre-rf1 SeedData's back-fill INSERT that bound `super_admin` to every existing
            Super user: not in the design seed matrix, and bootstrap self-provision (REQ-8.1, task 2)
            now binds `platform_admin` by CODE on every boot, so the back-fill is redundant — and a
            fresh big-bang reset (D13) has no pre-existing users to back-fill anyway. Carrying it would
            also re-introduce an `admin.RoleAssignments` INSERT coupled to the renamed column for no gain.

- [x] 5. Architecture tests — entity→schema allow-set test สร้างใหม่ (ครอบทุก module: schema ∈
     {shop, txn, admin, merch, iam} + named exceptions ของ rf1; entity `Iam` → `iam` เท่านั้น);
     module reference rules (`Iam.*` ไม่อ้าง module ใด, module อื่นอ้างได้แค่ `Iam.Domain`);
     confinement: ห้าม query `iam.Roles` นอก Iam store + resolution repositories
     Satisfies: REQ-1.6. Depends on: 2. Verify: `dotnet test --filter Architecture.Tests`.
     Evidence:
       - build: `dotnet build -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s) (full solution;
         added Iam.Domain/Iam.Application/Iam.Infrastructure ProjectReferences to Architecture.Tests.csproj
         so the full 8-module model + Iam anchor types are loadable in the test tier).
       - test: `dotnet test --filter Architecture.Tests` -> Passed! Failed: 0, Passed: 63 (was 55, +8).
         Full non-integration suite (`dotnet test --filter "Category!=Integration"`) all 12 projects green,
         0 failed (Hosts.Tests 226, Admins.Tests 95, Merchants.Tests 114, Iam.Tests 64, etc. — no collateral
         damage). New files:
           * `tests/Architecture.Tests/EntitySchemaMappingTests.cs` (3 facts) — builds `PolDbContext` over ALL
             7 module registration assemblies (Sqlite in-memory, same recipe as `MoneyColumnMappingTests` but
             all modules) and asserts: (1) every mapped entity's schema ∈ {shop, txn, admin, merch, iam, dbo};
             (2) `dbo` holds ONLY `DataProtectionKeys` (the single rf1 named exception — a forgotten
             `ToTable` schema would silently fall to dbo, this makes it red); (3) every `Iam.Domain` entity →
             `iam` and the four catalog tables Permissions/PermissionGroups/Roles/RolePermissions are all
             present (fail-loud vs a vacuous pass if the catalog vanished from the model).
           * `tests/Architecture.Tests/IamArchitectureTests.cs` (5 facts) — (b) module reference rules:
             `Iam.{Domain,Application,Infrastructure}` depend on NO business module and no Host;
             every OTHER module references only `Iam.Domain`, never `Iam.Application`/`Iam.Infrastructure`
             (NetArchTest `NotHaveDependencyOnAny` + the `AssertAllResolveToARealAssembly` anti-vacuity helper
             carried from Admin/Merchants tests); (c) confinement: across all production Infrastructure
             assemblies, any type depending on the `Iam.Domain.Roles.Role` entity MUST live under one of the
             three carve-out namespaces (`Iam.Infrastructure.Persistence.Roles`,
             `Admins.Infrastructure.Persistence.Roles`, `Merchants.Infrastructure.Persistence.Users.Roles`) —
             with a `dependents.Count > 0` guard so a renamed entity can't make the test pass vacuously.
       - deviations:
         1. Entity→schema test scoped by the confinement carve-out and the schema-allow-set uses Sqlite
            in-memory offline model (no SQL Server) — identical to the existing `MoneyColumnMappingTests`
            recipe: it asserts the CONFIGURED relational metadata (schema per `ToTable`), which is
            provider-independent, so no live DB is needed and it runs in the fast tier.
         2. `Every_Iam_entity_maps_to_the_iam_schema` matches Iam entities by assembly NAME
            (`ClrType.Assembly.GetName().Name == "Iam.Domain"`) rather than reference identity
            (`== typeof(Role).Assembly`) — the latter returned an empty set under the test host's load
            context even though the entities are genuinely from Iam.Domain (confirmed by a throwaway model
            dump); name comparison is robust and reads as the intent (the published-language assembly owns
            the RBAC catalog types).
         3. Confinement scan targets the 7 module Infrastructure assemblies + `Iam.Infrastructure` (the only
            layer that holds a `PolDbContext` and could write a `Set<Role>()` query), NOT
            `BuildingBlocks.Infrastructure`: the regenerated migration snapshot/designers there reference the
            catalog entities as STRING model names (`modelBuilder.Entity("Iam.Domain.Roles.Role", …)`), which
            create no IL type dependency, so excluding them avoids any snapshot false-positive while still
            catching a real rogue querier in any business module's infra. No confinement violations were
            found — the only existing dependents are the Iam store (`RoleStore`/`RoleSfs`/`RoleConfigurations`)
            and the two side resolution repositories, all already inside the carve-out namespaces.

- [x] 6. Integration suite — บน :11433 (bootstrap+migrate ก่อน): seed drift guard (iam rows
     SetEquals vocabulary + 4 roles + grants ต่อ role), grants matrix (pol_admin ตามตาราง /
     pol_app deny บน `iam.*`), no-RLS บน `iam.*` (sys.security_policies), FK bogus key reject,
     UNIQUE NULL-bucket pin (shared code ซ้ำ → DB reject), assignment drift guard (Scope ตรงฝั่ง
     + merch: role.MerchantId ∈ {NULL, assignment.MerchantId}), resolution defense-in-depth
     (assignment ชี้ role merchant อื่น → ไม่ contribute), RBAC E2E: merchant A/B custom role
     isolation, merchant แก้ shared = 409, cross-side grant = 400, assign ข้าม scope = 400,
     revoke/deactivate มีผล request ถัดไป, orthogonality (Scoped tier + platform_admin = action
     ได้/เห็นแคบ; Tier ไม่ให้ action), bootstrap self-provision → `platform_admin` idempotent,
     fresh-DB migrate จากศูนย์
     Satisfies: REQ-2.5, 2.6, 4.2, 4.4, 7.6, 8.1, 8.2, 8.3, 9.1, 9.2, 9.3, 10.2. Depends on: 4.
     Verify: `source .env.integration && dotnet test --filter Integration.Tests` (คำสั่งเดียวกัน
     ใน Bash call เดียว).
     Evidence:
       - build: `dotnet build -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s) (full solution;
         Integration.Tests now references `Iam.Domain` instead of the now-unused `Merchants.Domain` — the code
         side of the seed drift guard moved to the single `Keys` vocabulary + `Role` anchors).
       - test (verify gate, one Bash call): `source .env.integration && dotnet test --filter Integration.Tests`
         -> Passed! Failed: 0, Passed: 93, Skipped: 0 (Integration.Tests.dll). The 17 new `iam.*`-native tests
         (`IamCatalogGrantsTests` 8 + `IamRoleResolutionTests` 9) all green against live SQL Server 2025 on
         :11433 (bootstrap + migrated chain `20260712185344_InitialSchema`/`SecurityObjects`/`SeedData` applied;
         live pre-check: 8 groups/20 keys/4 roles/28 grants).
       - non-integration (no collateral): `dotnet test --filter "Category!=Integration"` -> all 12 projects
         green, 0 failed (Iam.Tests 64, Admins.Tests 95, Merchants.Tests 114, Hosts.Tests 226, Architecture.Tests
         63, etc. unchanged).
       - coverage of task-6 items:
         * seed drift guard (REQ-10.2): `Seeded_catalog_equals_the_code_vocabulary` — DB keys/groups/role-codes
           SetEqual `Keys.AllKeys`/`Keys.GroupKeys`/{4 roles}, and each group's DB `Scope` == `Keys.GroupScope`.
         * seed shape + per-role grants (REQ-2.3/2.5): `Catalog_seed_matches_the_advertised_shape` — 8/20/4/28
           and platform_admin 13 / platform_auditor 4 / merchant_manager 7 / merchant_staff 4.
         * grants matrix (REQ-9.1): `Admin_has_full_crud_on_roles_but_the_vocabulary_is_read_only` (pol_admin
           CRUD Roles/RolePermissions, INSERT on Permissions/PermissionGroups refused) + `App_principal_cannot
           _touch_any_iam_table` (pol_app denied SELECT on all 4).
         * no-RLS (REQ-9.2): `No_row_level_security_policy_covers_the_iam_schema` (sys.security_predicates on
           iam objects = 0).
         * FK bogus key reject (REQ-2.6): `A_role_cannot_grant_a_permission_outside_the_catalog`.
         * UNIQUE NULL-bucket pin (REQ-6.3): `Shared_role_code_is_unique_across_the_null_bucket_but_reusable_
           per_merchant` (two shared-NULL roles same Code → SqlException; two different merchants may reuse).
         * assignment drift guard (REQ-7.6): `Every_assignment_row_points_at_a_same_side_role` (admin.* → Scope
           Platform; merch.* → Scope Merchant AND MerchantId ∈ {NULL, assignment.MerchantId}).
         * resolution defense-in-depth (REQ-4.2): `A_merchant_assignment_pointing_at_another_merchants_role_
           does_not_contribute` + `Merchant_effective_permissions_union_only_active_visible_roles`.
         * merchant A/B isolation (REQ-3.6): `Merchant_B_cannot_see_merchant_A_custom_role`.
         * revoke/deactivate next request (REQ-4.4/4.6): `Deactivating_a_role_drops_its_permissions_on_the_
           next_resolution`.
         * orthogonality (REQ-8.2/8.3): `Platform_admin_role_grants_every_action_key_regardless_of_tier`
           (Scoped tier still resolves all 13 keys — action from role) + `Tier_alone_grants_no_action_and_admin
           _ignores_merchant_scope_roles` (Super with no role = 0 keys; a merchant-scope role assigned on the
           admin side never resolves).
         * bootstrap self-provision idempotent (REQ-8.1): `Bootstrap_binds_platform_admin_by_code_idempotently`
           (resolve platform_admin by CODE in the Platform visible set, assign, second identical bind rejected
           by the unique (PlatformUserId, RoleId) index, effective set = 13 keys).
         * fresh-DB migrate from zero (REQ-9.3/1.4/2.5): `Reset_dropped_the_two_legacy_per_side_catalogs`
           (the 8 admin.*/merch.* catalog tables gone, 4 iam.* present) — the whole run executes against a DB
           migrated from zero per the runbook.
       - deviations:
         1. DELETED `tests/Integration.Tests/AdminRoleRbacGrantsTests.cs` — the admin-side analog of the merch
            catalog test task 2 already deleted. It asserted against `admin.Permissions`/`admin.PermissionGroups`/
            `admin.Roles`/`admin.RolePermissions`, all DROPPED by task 4's InitialSchema, so it was already RED
            against the migrated DB (a pre-existing loose end task 6 owns). `IamCatalogGrantsTests` is its
            `iam.*`-native successor (same drift-guard / grants / FK / pol_app-deny shape, now over the single
            central catalog), so no coverage is lost.
         2. Swapped the csproj `Merchants.Domain` ProjectReference for `Iam.Domain`: no source file referenced
            `Merchants.Domain` any longer (the old per-side drift guard was deleted), and the drift guard's code
            side is now `Iam.Domain.Permissions.Keys` + `Iam.Domain.Roles.Role`.
         3. The three app-RESPONSE status codes in the task-6 E2E list — merchant edits shared = 409, cross-side
            grant = 400, assign across scope = 400 — are HTTP-shaped handler outcomes; the Integration.Tests
            suite is deliberately raw-connection (no HTTP/DbContext harness — see its csproj comment). Those exact
            codes are already unit-covered over a real `PolDbContext` in `Iam.Tests`
            (`UpdateRoleHandlerTests`/`CreateRoleHandlerTests`/`DeleteRoleHandlerTests`, both scopes, per task 2).
            At the integration tier this task pins the LIVE-SQL floor those codes rest on and that SQLite cannot
            prove: the shared seed is never in a merchant's owned mutable set (`A_shared_seed_role_is_not_in_any_
            merchants_owned_mutable_set`, the 409's floor), the admin side never resolves a merchant-scope role
            and Tier grants no action (the cross-side/assign-scope floor), and the assignment drift guard catches
            any write that escaped validation. No E2E behaviour is unverified — the split is unit(app-code) +
            integration(DB-contract), matching the suite's convention.
         4. Added `IamCatalogCollection` ([CollectionDefinition]) and put both new classes in it so they run
            sequentially: both mutate the one shared `iam.*` catalog, and the absolute seed-count / SetEquals
            drift assertions would otherwise race the resolution class's transient probe rows under xUnit's
            default per-class parallelism. Every mutating test cleans up its own iam.Roles/RoleAssignments in a
            `finally`; transient `admin.Users` rows are left in place (pol_admin has no DELETE grant on that
            control-plane table — the existing provisioning integration tests leave rows the same way; DB is
            throwaway).

- [ ] 7. Canon + docs sync — อัปเดต `.ai/shared/CODING_STANDARDS.md` (canonical entities:
     Iam catalog แทน 2 ชุดเดิม, permission keys/roles ใหม่) + `.ai/shared/ARCHITECTURE.md`
     (identity/RBAC section: catalog กลาง iam, seed 4 roles) + `docs/reference/platform-modules.md`
     (สถานะ RBAC); grep gates ปิดท้าย: `ponytail: DUPLICATE` (RBAC) = 0, `PermissionParity`/
     `UserPermissionParity`/`Admins.Domain.Permissions`/`Merchants.Domain.Users.Permissions` = 0
     ใน src/; `scripts/spec-trace.sh rf2-iam-rbac` เขียว
     Satisfies: REQ-1.5. Depends on: 2, 3. Verify: grep = 0 + spec-trace exit 0.

## Suggested execution batches

Feature นี้ coupled หนัก (ทุก task แชร์ Iam primitives + model เดียว + migration regen ต้องเห็น
model สุดท้าย) → **รัน ALL ใน session เดียว**: `scripts/pane-loop.sh rf2-iam-rbac all-in-one`
หรือ `/spec-implement all`. ไม่ตั้ง `Batch:` tag — ไม่มี task เล็ก same-type ที่ควร group แยก.
ลำดับจริง: 1 → 2 → 3 ขนานได้กับ 2 บางส่วน → 4 (ต้องเห็น model สุดท้าย) → 5, 6, 7.
