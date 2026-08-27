# Tasks: bugfix-merchant-prebind-wiring
> Status: approved 2026-07-26

- [x] T1. RED repro tests (ต้องแดงก่อนแก้ ทุก assertion เช็ค observable behavior)
  - lifecycle test ใหม่ใน `tests/Architecture.Tests/` (SQLite in-memory + real
    `MerchantUserDbContext`/adapters + real `MerchantRequestWriteAuthorizer`, actor
    `HasActor=false` — pattern จาก `MerchantRegistrationWriterTests` /
    `PreBindWritePortTests`): submit Registration -> resolve login (expect
    PendingApproval, วันนี้ NotFound = แดง) -> reject -> resolve (expect Rejected) ->
    Correction submit (expect resubmit 1 แถวเดิม) -> approve พร้อม role (expect Active +
    RoleAssignment + audit, วันนี้ NotFound/WriteGuard = แดง) -> resolve (expect Active)
  - `tests/Hosts.Tests/`: `HttpActorContext` — claim `merchant_id` ที่ถูกตั้งบน
    `HttpContext.User` หลัง construct ต้องมองเห็น (`HasActor=true`, `MerchantId`=claim)
    — วันนี้แดง (ctor snapshot)
  - pin (เขียวตั้งแต่วันนี้ — บันทึกเหตุผลของ seam split): filtered
    `MerchantUserRepository.FindBySubjectAsync` คืน null สำหรับแถว pending ใต้ unbound actor
     Satisfies: F-1, F-2, F-3, F-5, F-6
  Evidence:
    - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~MerchantIdentityLifecycleTests"` -> RED ตามคาด 7 failed / 1 passed (pin เขียว): resolve คืน NotFound แทน PendingApproval/Rejected, approve/reject โดน NotFoundException, correction โดน InvalidOperationException
    - test: `dotnet test tests/Hosts.Tests --filter "FullyQualifiedName~HttpActorContextTests|FullyQualifiedName~MerchantRequestWriteAuthorizerTests"` -> RED ตามคาด 2 failed (claim หลัง construct มองไม่เห็น = D3) / 4 passed (ambient precedence, dev fallback, D2 boundary pins ของ MerchantRequestWriteAuthorizer จริง)
    - viewports: n/a — logic-only
    - deviations: lifecycle test ใช้ floor mirror ของ `MerchantRequestWriteAuthorizer` แทน class จริง เพราะ class เป็น internal ของ Api (InternalsVisibleTo เฉพาะ Hosts.Tests โดย design) — class จริงถูก pin ตรง ๆ ใน Hosts.Tests แทน (ไฟล์เดียวกับ HttpActorContextTests)

- [x] T2. D1 fix: port surface + adapters + DI flip
  - `Merchants.Application/Users/UserPorts.cs`: เพิ่ม `AccountSnapshot` record +
    `IAccountResolver` (FindBySubjectAsync/FindByIdAsync, AsNoTracking, filter-free) +
    `IAccountStore` (tracked filter-free FindBySubjectAsync + Add); อัปเดต doc comment
    `IUserRepository` = bound in-session เท่านั้น
  - handler switch: `ResolveLogin.cs` + `ResolveById.cs` -> `IAccountResolver`;
    `SubmitRegistration.cs` + `ApproveReject.cs` (ทั้ง Approve/Reject) -> `IAccountStore`
    — logic ภายใน handler คงเดิมทุกบรรทัด เปลี่ยนเฉพาะ seam
  - `Persistence.MerchantUsers/Users/`: ขยาย `MerchantResolveLoginBySubject.cs` เป็น impl
    `IAccountResolver` (เพิ่ม by-id projection); ใหม่ `MerchantAccountStore.cs` impl
    `IAccountStore` (`IgnoreQueryFilters()` tracked); ลบ `MerchantRegistrationSubmitWriter.cs`
    + `MerchantRegistrationWriter.cs` (+ tests ของมัน + สอง section ใน
    `PreBindWritePortTests`); register ports ใหม่ใน `MerchantUserPersistenceRegistration.cs`
  - `tests/Architecture.Tests/BypassPrimitiveTests.cs` allowlist: +`MerchantAccountStore.cs`
    −writer 2 ไฟล์; `tests/Merchants.Tests/` fakes implement interface ใหม่ (mechanical)
     Satisfies: F-1, F-2, F-4, F-6, B-1, B-2, B-7
  Evidence:
    - test: `dotnet build` -> ok 64 projects, 0 errors, 0 warnings
    - test: `dotnet test tests/Architecture.Tests --filter "FullyQualifiedName~MerchantIdentityLifecycleTests|FullyQualifiedName~PreBindReadPortTests|FullyQualifiedName~PreBindWritePortTests|FullyQualifiedName~BypassPrimitiveTests"` -> 21 passed / 0 failed (lifecycle resolve/correction/reject/by-id เขียวหลัง DI flip)
    - test: `dotnet test tests/Merchants.Tests` -> 115 passed / 0 failed (fakes สลับเป็น IAccountStore/IAccountResolver แล้ว behavior เดิมทุกข้อ)
    - viewports: n/a — logic-only
    - deviations: แทนที่จะ extend `MerchantResolveLoginBySubject.cs` ตามแผน — ลบไฟล์แล้วสร้าง `MerchantAccountResolver.cs` แทน (ชื่อ class ตรงกับ member ใหม่ by-subject + by-id); allowlist สุทธิ -3 +2

- [x] T3. D2 fix: admin approval write capability
  - `src/Hosts/Api/Persistence/WriteAuthorizers.cs`: `AdminApprovalWriteAuthorizer(IAdminScope)`
    — allowlist แคบ (User, Update) / (RoleAssignment, Insert) / (RegistrationAudit, Insert)
    เมื่อ `targetMerchant == Guid.Empty || scope.Accessible.Allows(targetMerchant)`;
    อื่น deny (mirror `AdminItemPolicyWriteAuthorizer`)
  - `src/Hosts/Api/Program.cs:171` `ResolveMerchantWriteAuthorizer` -> three-way:
    background scope = Worker / HTTP + `IAdminScope.IsBound` = AdminApproval / else = MerchantRequest
  - unit tests: deny product-plane types, deny merchant นอก accessible set, allow ชุด approve
    ใน scope; composition-root pin เลือก authorizer ถูกตัวทั้ง 3 ทาง
     Satisfies: F-3, B-3, B-4
  Evidence:
    - test: `dotnet test tests/Hosts.Tests` -> 341 passed / 0 failed (รวม `AdminApprovalWriteAuthorizerTests` 3 ข้อ: allow ชุด approve ใน scope / confine Scoped admin / deny นอกชุด + `HttpMerchantWriteAuthorizerSelectionTests` 3 ข้อ + boot/composition tests เดิมทั้งหมด)
    - test: lifecycle `An_admin_approve_activates_binds_the_merchant_and_assigns_the_role` + `Full_lifecycle_...` เขียว (approve ผ่าน write floor แล้ว)
    - viewports: n/a — logic-only
    - deviations: การเลือก admin-vs-merchant ทำต่อ write ผ่าน `HttpMerchantWriteAuthorizer` (per-call, อ่าน `IAdminScope.IsBound` ตอน CanWrite) แทน three-way ตอน construct — กัน construction-order hazard แบบเดียวกับ D3; composition-root pin ทำผ่าน `HttpMerchantWriteAuthorizerSelectionTests` + boot tests เดิม (static local function ใน Program.cs address ตรงไม่ได้)

- [x] T4. D3 fix: `HttpActorContext` lazy claims
  - `src/Hosts/Api/HttpActorContext.cs`: ย้าย `FindFirstValue` จาก constructor ไป property
    getter (อ่านสดทุกครั้ง); precedence คงเดิม `AmbientActor` > claims > `Merchant:DevMerchantId`
  - `tests/Hosts.Tests/`: laziness test จาก T1 เขียว + precedence tests (ambient ชนะ claim,
    dev fallback เมื่อไม่มี claim)
     Satisfies: F-5, B-10, B-11
  Evidence:
    - test: `dotnet test tests/Hosts.Tests` -> 341 passed / 0 failed — `HttpActorContextTests` เขียวครบ 4: claim หลัง construct มองเห็นแล้ว (F5, แดงใน T1), ambient ชนะ claim (B10), dev fallback เมื่อไม่มี claim + claim ชนะ fallback (B11)
    - viewports: n/a — logic-only
    - deviations: none

- [x] T5. GREEN + B-coverage sweep + docs + full gate
  - lifecycle test T1 เขียวครบทุก step; suite เดิมยืนยัน B:
    `MerchantUserLoginServiceTests` (B5 suspended, B6 awaiting-approval),
    `SubmitRegistrationHandlerTests` (B7 correction non-rejected refused),
    `ApproveRejectMerchantUserTests` (B8 404/409 + idempotent), integration
    `A_second_account_for_the_same_subject_is_rejected_by_the_unique_index` (B1)
  - B9: `git status` ยืนยันไม่มี migration/schema/contract file ถูกแตะ; B2: pin test bound
    actor เห็นเฉพาะ merchant ตัวเอง ผ่าน `IUserRepository`
  - docs: doc comment `MerchantUserPersistenceRegistration` ("all 9 ports" count) +
    `MerchantUserRepository`; note supersede ใน `.ai/specs/rls-to-query-filter/tasks.md`
    task 8; `docs/reference/merchant-user-module.md` ถ้าอ้าง writer ports ที่ลบ
  - gate: `dotnet build -warnaserror` 0 warning; `dotnet test` ทุก suite green;
    integration local (source `.env.integration` ใน Bash call เดียวกับ `dotnet test`)
     Satisfies: F-1, F-2, F-3, F-4, F-5, F-6, B-1, B-2, B-3, B-4, B-5, B-6, B-7, B-8, B-9, B-10, B-11
  Evidence:
    - test: `dotnet build -warnaserror` -> ok 64 projects, 0 errors, 0 warnings
    - test: `dotnet test tests/Architecture.Tests` -> 213 passed / 0 failed (8m59s — รวม lifecycle 8, PreBindRead 6, PreBindWrite 5, BypassPrimitive 2)
    - test: `dotnet test tests/Hosts.Tests` -> 341 passed / 0 failed (B5/B6 ผ่าน `MerchantUserLoginServiceTests`, B8 ผ่าน host guard tests เดิม, B10/B11 ผ่าน `HttpActorContextTests`, B4 ผ่าน `MerchantRequestWriteAuthorizerTests`)
    - test: `dotnet test tests/Merchants.Tests` -> 115 passed / 0 failed (B1 dedup 409, B7 correction non-rejected refused, B8 404/409/idempotent)
    - test: `source .env.integration` + `dotnet test tests/Integration.Tests` -> 44 passed / 0 failed บน SQL Server จริง :11433 (B1 unique index จริง)
    - test: suite อื่นทั้ง solution เขียว (SharedKernel 46, BuildingBlocks 43, Orders 68, Payments 59, Iam 62, Admins 95, Products/Carts/Checkouts/Divisions/Levels/Offices/Positions ครบ 0 failed)
    - test: `scripts/check-rename-identifiers.sh` -> OK; `.ai/bin/check-secrets.sh --all` -> exit 0; `scripts/spec-trace.sh` -> bugfix spec ข้าม traceability ตามกติกา
    - test: B9 — `git status` diff 29 ไฟล์: ไม่มี migration/schema/contract/.env* ถูกแตะ (docs + src identity paths + tests เท่านั้น)
    - viewports: n/a — logic-only
    - deviations: docs อัปเดต 3 จุด (merchant-user-module.md write-floor/carve-out/ports table, rls-to-query-filter tasks.md supersede note, doc comments ใน UserPorts/MerchantUserRepositories); `MerchantUserPersistenceRegistration` class doc "all 9 ports" คงไว้ (เป็น historical statement ของ task 8.5.2)
