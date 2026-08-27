# Implementation Tasks: Registration Attempt History

> Status: approved 2026-08-02
> Notes:, amended 2026-08-02 (REQ-2.7 — review PR #161)

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Attempt snapshot capture end-to-end — entity `RegistrationAttempt` + EF config 2 ฝั่ง (FK ประกาศทั้งคู่ + unique `(MerchantUserId, AttemptNo)` + index `RegistrationAudits(TargetSubject)`) + DbSet + scaffold migration `AddRegistrationAttempts` + `AppendOnlyDescriptor` + เพิ่ม `typeof(RegistrationAttempt)` ลง `MerchantRequestWriteAuthorizer.OwnedTypes` + port `IRegistrationAttemptWriter` + adapter + เขียน snapshot ใน `SubmitRegistrationHandler` ทั้ง 2 branch (tx เดิม, `Email` จาก command); done = ทั้ง 2 branch เขียน attempt ถูกต้อง, append-only guard ทำงาน, fresh-db `ef database update` ผ่าน
     REQ-1 (all criteria).
       - deviations: refactor 2 branch ของ handler ให้ hoist `account`/`action` ร่วม (displayName ใช้จาก account ตรง) — เท่า design "โค้ดร่วมหลัง if/else"
- [x] 2. Permission `merchants.users.view` + grant + seed — `Keys.cs` เพิ่ม `MerchantUserView` (update `GroupKeys`/`All`/XML doc 22→23) + `RegistrationAuditAction.Revealed` + migration `GrantAndSeedRegistrationHistory` (scaffold เปล่าแล้ว hand-edit: GRANT `SELECT, INSERT` บน `merch.RegistrationAttempts` ให้ `pol_app`, INSERT `iam.Permissions` SortOrder 25 + `iam.RolePermissions` ให้ `platform_admin`, `Down()` ย้อนกลับ) + `assert-fresh-db.sql` (Permissions 23, RolePermissions 31) + pins: `tests/Iam.Tests/KeysTests.cs`, `tests/Integration.Tests/IamCatalogGrantsTests.cs`; done = fresh-db replay ผ่าน + counts ตรง
     REQ-4.1, REQ-4.4. Depends on: 1.
       - deviations: none
- [x] 3. Query ประวัติ + masking + reveal audit — `GetRegistrationHistory.cs` (query/handler/DTO/`PiiMask`) + port `IRegistrationHistoryReader` (AsNoTracking, ตัด action `revealed`, ORDER ตาม design) + adapter + handler ใช้ `IAccountResolver` (404 = null) + audit `revealed` persist ผ่าน `IRegistrationAuditWriter`+`IUserUnitOfWork` ก่อนประกอบ DTO (precedent `GetOrderDetailHandler`); done = handler ครบทุกพฤติกรรม REQ-2 (ยกเว้น endpoint) + REQ-3
     REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7 (amended — review PR #161), REQ-3 (all criteria). Depends on: 1.
       - deviations: `PiiMask` เป็น internal (ไม่มี InternalsVisibleTo) — ทดสอบ mask edges ผ่านผลลัพธ์ handler แทนการเรียกตรง ครอบเคสเท่ากัน
- [x] 4. Endpoint admin + gate + e2e + docs — `Program.cs` MapGet `/merchants/users/{subject}/registrations` (`bool reveal = false`, `RequirePermission(Keys.MerchantUserView)`, OpenAPI metadata) + pins `PermissionGateSitesTests` (25→26) / `PermissionAuthorizationTests.RealGateSites` + Hosts.Tests (403 ไม่มี key, 200 มี key, ไม่ส่ง `?reveal=` → 200 masked) + ขยาย `MerchantIdentityLifecycleTests` (register→reject→resubmit → 2 attempts ใต้ `MerchantUserId` เดียว + endpoint timeline ครบ) + docs `docs/reference/merchants.md`/`iam.md`; done = full gate เขียว
     REQ-2.1, REQ-4.2, REQ-4.3, REQ-1.3. Depends on: 2, 3.
| REQ-4.2, 4.3 | 4 |
