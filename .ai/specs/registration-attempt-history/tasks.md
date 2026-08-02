# Implementation Tasks: Registration Attempt History

> Status: approved 2026-08-02

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Attempt snapshot capture end-to-end — entity `RegistrationAttempt` + EF config 2 ฝั่ง (FK ประกาศทั้งคู่ + unique `(MerchantUserId, AttemptNo)` + index `RegistrationAudits(TargetSubject)`) + DbSet + scaffold migration `AddRegistrationAttempts` + `AppendOnlyDescriptor` + เพิ่ม `typeof(RegistrationAttempt)` ลง `MerchantRequestWriteAuthorizer.OwnedTypes` + port `IRegistrationAttemptWriter` + adapter + เขียน snapshot ใน `SubmitRegistrationHandler` ทั้ง 2 branch (tx เดิม, `Email` จาก command); done = ทั้ง 2 branch เขียน attempt ถูกต้อง, append-only guard ทำงาน, fresh-db `ef database update` ผ่าน
     Satisfies: REQ-1 (all criteria). Verify: `dotnet test tests/Merchants.Tests` (SubmitRegistrationHandlerTests ขยาย: attempt ทั้ง 2 branch, AttemptNo 1→2, Email จาก command, photo reference, writer fail → throw) + append-only test (`WriteGuardException` ข้อความ append-only) + `ModelConsistencyTests`.
     Evidence:
       - test: `dotnet test tests/Merchants.Tests` -> 123 passed / 0 failed (3 attempt tests ใหม่)
       - test: `dotnet test tests/Architecture.Tests --filter WriteFloorTests|MerchantIdentityLifecycleTests` -> 19 passed / 0 failed (รวม Registration_attempt_accepts_insert_but_rejects_modify_and_delete)
       - test: `dotnet test tests/Hosts.Tests --filter ModelConsistencyTests` -> 1 passed
       - migration: `dotnet ef database update` (20260802082339_AddRegistrationAttempts) -> Done บน dev DB :11433
       - viewports: n/a — logic-only
       - deviations: refactor 2 branch ของ handler ให้ hoist `account`/`action` ร่วม (displayName ใช้จาก account ตรง) — เท่า design "โค้ดร่วมหลัง if/else"
- [x] 2. Permission `merchants.users.view` + grant + seed — `Keys.cs` เพิ่ม `MerchantUserView` (update `GroupKeys`/`All`/XML doc 22→23) + `RegistrationAuditAction.Revealed` + migration `GrantAndSeedRegistrationHistory` (scaffold เปล่าแล้ว hand-edit: GRANT `SELECT, INSERT` บน `merch.RegistrationAttempts` ให้ `pol_app`, INSERT `iam.Permissions` SortOrder 25 + `iam.RolePermissions` ให้ `platform_admin`, `Down()` ย้อนกลับ) + `assert-fresh-db.sql` (Permissions 23, RolePermissions 31) + pins: `tests/Iam.Tests/KeysTests.cs`, `tests/Integration.Tests/IamCatalogGrantsTests.cs`; done = fresh-db replay ผ่าน + counts ตรง
     Satisfies: REQ-4.1, REQ-4.4. Depends on: 1. Verify: `dotnet test tests/Iam.Tests` + Integration.Tests (DB จริง :11433) + `docker/bootstrap/assert-fresh-db.sql` ผ่าน.
     Evidence:
       - test: `dotnet test tests/Iam.Tests` -> 61 passed / 0 failed (pins 23 keys / platform 16)
       - test: `dotnet test tests/Integration.Tests --filter IamCatalogGrantsTests|RegistrationAttemptGrantsTests` -> 10 passed / 0 failed (counts 9/23/4/31 บน DB จริง, RegistrationAttemptGrantsTests ใหม่ 3 ข้อ: SELECT+INSERT ผ่าน, UPDATE/DELETE denied, unique index 2601/2627)
       - migration: `dotnet ef database update` (20260802082629_GrantAndSeedRegistrationHistory) -> Done
       - sql: `sqlcmd -i assert-fresh-db.sql` -> "assert-fresh-db: OK"
       - viewports: n/a — logic-only
       - deviations: none
- [x] 3. Query ประวัติ + masking + reveal audit — `GetRegistrationHistory.cs` (query/handler/DTO/`PiiMask`) + port `IRegistrationHistoryReader` (AsNoTracking, ตัด action `revealed`, ORDER ตาม design) + adapter + handler ใช้ `IAccountResolver` (404 = null) + audit `revealed` persist ผ่าน `IRegistrationAuditWriter`+`IUserUnitOfWork` ก่อนประกอบ DTO (precedent `GetOrderDetailHandler`); done = handler ครบทุกพฤติกรรม REQ-2 (ยกเว้น endpoint) + REQ-3
     Satisfies: REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6, REQ-3 (all criteria). Depends on: 1. Verify: `dotnet test tests/Merchants.Tests` (GetRegistrationHistoryHandlerTests ใหม่: mask edges, reveal + audit persist รวม list ว่าง, 404 ไม่ audit, audit fail → throw, เรียง AttemptNo, timeline ไม่มี `revealed`).
     Evidence:
       - test: `dotnet test tests/Merchants.Tests` -> 131 passed / 0 failed (GetRegistrationHistoryHandlerTests ใหม่ 8 ข้อครบทุกพฤติกรรม)
       - viewports: n/a — logic-only
       - deviations: `PiiMask` เป็น internal (ไม่มี InternalsVisibleTo) — ทดสอบ mask edges ผ่านผลลัพธ์ handler แทนการเรียกตรง ครอบเคสเท่ากัน
- [x] 4. Endpoint admin + gate + e2e + docs — `Program.cs` MapGet `/merchants/users/{subject}/registrations` (`bool reveal = false`, `RequirePermission(Keys.MerchantUserView)`, OpenAPI metadata) + pins `PermissionGateSitesTests` (25→26) / `PermissionAuthorizationTests.RealGateSites` + Hosts.Tests (403 ไม่มี key, 200 มี key, ไม่ส่ง `?reveal=` → 200 masked) + ขยาย `MerchantIdentityLifecycleTests` (register→reject→resubmit → 2 attempts ใต้ `MerchantUserId` เดียว + endpoint timeline ครบ) + docs `docs/reference/merchants.md`/`iam.md`; done = full gate เขียว
     Satisfies: REQ-2.1, REQ-4.2, REQ-4.3, REQ-1.3. Depends on: 2, 3. Verify: `dotnet test tests/Hosts.Tests` + Architecture.Tests lifecycle filter + full gate ก่อนเปิด PR.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests` -> 383 passed / 0 failed (RegistrationHistoryEndpointTests ใหม่ 3 ข้อ: 403 ไม่มี key, 200+masked เมื่อไม่ส่ง ?reveal=, 404 unknown; gate pins 26 sites)
       - test: `dotnet test tests/Architecture.Tests --filter MerchantIdentityLifecycleTests` -> 11 passed (lifecycle e2e ใหม่: 2 attempts ใต้ MerchantUserId เดียว + timeline ครบ reject reason)
       - test: full suite `dotnet test` -> ทุก project เขียว: SharedKernel 46, BuildingBlocks 43, Products 137, Carts 15, Payments 162, Checkouts 13, Orders 76, Iam 61, Merchants 131, Admins 95, Integration 116, Architecture 202, Hosts 383 (+ref modules 24)
       - trace: `scripts/spec-trace.sh registration-attempt-history` -> OK 26 เกณฑ์ครบ
       - viewports: n/a — logic-only
       - deviations: full run แรกเจอ pin เก่า 15-key ของ platform_admin ใน `IamRoleResolutionTests` (2 ข้อ) — อัปเดตเป็น 16 แล้วรันซ้ำเขียว; header comment ของ PermissionGateSitesTests ตัวเลขเก่า stale (26 vs Fact 25) แก้ให้ตรงจริงเป็น 8+18=26

## Suggested execution batches

> Feature นี้ COUPLED ทั้งสาย (entity → permission → query → endpoint แชร์ type/migration chain เดียวกัน)
> — รัน ALL ใน session เดียว: `/spec-implement all` (หรือ `scripts/pane-loop.sh registration-attempt-history all-in-one`)
> ไม่ติด `Batch:` แยก — ไม่มี task ไหนอิสระพอให้แยก pane แล้วได้ประโยชน์

## Requirement Traceability

| REQ | Task |
|-----|------|
| REQ-1 (1.1-1.9) | 1 (1.3 ยืนยันซ้ำใน e2e ของ task 4) |
| REQ-2.1 | 4 |
| REQ-2.2-2.6 | 3 |
| REQ-3 (3.1-3.7) | 3 |
| REQ-4.1, 4.4 | 2 |
| REQ-4.2, 4.3 | 4 |
