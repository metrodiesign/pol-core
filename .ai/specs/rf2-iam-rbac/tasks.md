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
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. Architecture tests — entity→schema allow-set test สร้างใหม่ (ครอบทุก module: schema ∈
     {shop, txn, admin, merch, iam} + named exceptions ของ rf1; entity `Iam` → `iam` เท่านั้น);
     module reference rules (`Iam.*` ไม่อ้าง module ใด, module อื่นอ้างได้แค่ `Iam.Domain`);
     confinement: ห้าม query `iam.Roles` นอก Iam store + resolution repositories
     Satisfies: REQ-1.6. Depends on: 2. Verify: `dotnet test --filter Architecture.Tests`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 7. Canon + docs sync — อัปเดต `.ai/shared/CODING_STANDARDS.md` (canonical entities:
     Iam catalog แทน 2 ชุดเดิม, permission keys/roles ใหม่) + `.ai/shared/ARCHITECTURE.md`
     (identity/RBAC section: catalog กลาง iam, seed 4 roles) + `docs/reference/platform-modules.md`
     (สถานะ RBAC); grep gates ปิดท้าย: `ponytail: DUPLICATE` (RBAC) = 0, `PermissionParity`/
     `UserPermissionParity`/`Admins.Domain.Permissions`/`Merchants.Domain.Users.Permissions` = 0
     ใน src/; `scripts/spec-trace.sh rf2-iam-rbac` เขียว
     Satisfies: REQ-1.5. Depends on: 2, 3. Verify: grep = 0 + spec-trace exit 0.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
