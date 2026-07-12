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

- [ ] 4. Migration chain regen + seed + grants — regenerate 3 migrations แบบ EF-native
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

- [ ] 5. Architecture tests — entity→schema allow-set test สร้างใหม่ (ครอบทุก module: schema ∈
     {shop, txn, admin, merch, iam} + named exceptions ของ rf1; entity `Iam` → `iam` เท่านั้น);
     module reference rules (`Iam.*` ไม่อ้าง module ใด, module อื่นอ้างได้แค่ `Iam.Domain`);
     confinement: ห้าม query `iam.Roles` นอก Iam store + resolution repositories
     Satisfies: REQ-1.6. Depends on: 2. Verify: `dotnet test --filter Architecture.Tests`.

- [ ] 6. Integration suite — บน :11433 (bootstrap+migrate ก่อน): seed drift guard (iam rows
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
