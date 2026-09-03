# Implementation Tasks: Tier 0 Graph Employee Profile

> Status: approved 2026-08-30

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

Gate ต่อ task (ยกเว้นที่ระบุ): `dotnet build pol-core.slnx --no-restore -warnaserror` +
`dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`; task ที่แตะ DB รัน
`dotnet test pol-core.slnx --filter "Category=Integration"` ด้วย ทุกคำสั่ง `dotnet` รันเป็นคำสั่งเดี่ยว
(ไม่ cd/pipe ตาม lesson `sandbox-blocks-dotnet-test`)

- [x] 1. Domain model + schema foundation — `EmployeeIdPolicy` (pure), `User.EmployeeId/FirstName/LastName` +
     `ApplyEmployeeProfile` (writer เดียว, Version bump เฉพาะเมื่อเปลี่ยน), `AuditAction.EmployeeBind`,
     `Office.LegacyKey`/`Division.LegacyKey`, EF config mirror ทั้ง migration-owner และ runtime (User ×2,
     Office ×2, Division ×2), migration `Tier0EmployeeProfile` (5 คอลัมน์ + 3 filtered unique index +
     conditional GRANT, `Down()` ไม่แตะ HR tables), `check-migration-script.sh --write`,
     `assert-fresh-db.sql` 3 จุด, static gate ห้าม `EmployeeId =` นอก `User.cs`. Done = unit tests ของ
     policy/aggregate เขียว, `ef database update` บน fresh DB ผ่าน, migration integration test ยืนยันคอลัมน์/index/FK
     Satisfies: REQ-2.1-REQ-2.4, REQ-2.6-REQ-2.9, REQ-2.11, REQ-2.15-REQ-2.16, REQ-2.18, REQ-3.6-REQ-3.8, REQ-3.13-REQ-3.14, REQ-4.12, REQ-4.16-REQ-4.17, REQ-5.8, REQ-5.11-REQ-5.12, REQ-6.1-REQ-6.5, REQ-7.11-REQ-7.13, REQ-8.1-REQ-8.13, REQ-10.6-REQ-10.7
     Verify: `tests/Admins.Tests/EmployeeIdPolicyTests.cs`, `tests/Admins.Tests/UserEmployeeProfileTests.cs`,
     `tests/Integration.Tests/Tier0EmployeeProfileMigrationTests.cs`, `scripts/check-migration-script.sh`,
     `Tier0WorkforceArchitectureTests` (EmployeeId assignment gate).
     Evidence:
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> Admins.Tests 151 passed / 0 failed (EmployeeIdPolicyTests 14 + UserEmployeeProfileTests 8 ใหม่), Architecture.Tests 294 passed / 0 failed (gate `EmployeeId_is_assigned_only_inside_the_user_aggregate` ใหม่), ทุก project 0 failed
       - test: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Tier0EmployeeProfileMigrationTests"` -> 2 passed / 0 failed (fresh scratch DB `MigrateAsync()` = ef database update บน DB ว่าง; ยืนยัน 5 คอลัมน์ nvarchar NULL, 3 filtered unique index, FK uniqueidentifier ไป cfg.Offices/cfg.Divisions, GRANT pol_app เมื่อ HR tables มี, ไม่สร้าง HR tables, Down ลบเฉพาะ 5 คอลัมน์ + 3 index, seed 8/10 คงอยู่)
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded, 0 warning
       - script: `scripts/check-migration-script.sh --write` -> docker/migrations/schema.sql +104 บรรทัด (Tier0EmployeeProfile); `assert-fresh-db.sql` แก้ 3 จุด (VALUES, `<> 22` ×2, ข้อความ) + `assert-fresh-db.test.sh`, runbook 2 ไฟล์, `Tier0WorkforceIdentityMigrationSqlTests.CurrentMigration` ชี้ head ใหม่
       - viewports: n/a — logic-only
       - deviations: `dotnet build`/`dotnet ef` ต้องรันนอก sandbox (ใน sandbox ได้ `Build FAILED. 0 Error(s)` หลัง restore ค้าง 5 นาที ตาม Known failures); `assert-fresh-db.test.sh` รันใน sandbox ไม่ได้ (`mktemp ... Operation not permitted`) ยังไม่ได้รัน

- [x] 2. HR lookup port + reader — `IEmployeeProfileReader`/`EmployeeProfile`/`EmployeeProfileLookup` ใน
     `Admins.Application`, `EmployeeProfileReader` ใน `Persistence.ControlPlane` (`SqlQueryRaw` + `SqlParameter`
     ต่อ `cfg.VibEmp`/`cfg.branch`, LINQ ต่อ `Offices`/`Divisions` by `LegacyKey`, `SqlException` →
     `SourceUnavailable`, log เฉพาะ error number + correlation id), DI registration, `BypassPrimitiveTests`
     allowlist, integration fixture (สร้างตารางขั้นต่ำเมื่อไม่มี + GRANT `pol_app`, row ของตัวเองเท่านั้น
     prefix `ZTEST-`, คืน `LegacyKey` เป็น NULL ตอนจบ). Done = ทุก status ใน mapping table ของ design ถูกขับด้วย
     integration test ต่อ `pol_app` จริง รวม permission-denied case ใน throwaway DB
     Satisfies: REQ-3.1-REQ-3.5, REQ-3.9-REQ-3.12, REQ-3.15-REQ-3.18, REQ-4.1-REQ-4.10, REQ-4.13-REQ-4.15, REQ-4.18, REQ-5.1-REQ-5.6, REQ-5.9-REQ-5.10, REQ-5.13, REQ-6.6-REQ-6.7, REQ-7.10, REQ-11.7
     Depends on: 1
     Verify: `tests/Integration.Tests/EmployeeProfileReaderIntegrationTests.cs`,
     `tests/Admins.Tests/EmployeeProfileReaderStatusTests.cs` (fake rows: duplicate LegacyKey → Invalid),
     `BypassPrimitiveTests` เขียว.
     Evidence:
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build` -> 169 passed / 0 failed (EmployeeProfileReaderStatusTests 18 ใหม่: ทุกแถวของ status table ผ่าน fake source รวม duplicate LegacyKey → Invalid)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "Category!=Integration"` -> 294 passed / 0 failed (BypassPrimitiveTests เขียวหลัง allowlist `EmployeeProfileReader.cs`)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~EmployeeProfileReaderIntegrationTests"` -> 3 passed / 0 failed (scratch DB, connection เป็น login `pol_app` จริง: Found/Missing/Invalid ×3/Unmapped ×5/Inactive flags, trim + no-prefix, SELECT-only permission; ตารางไม่มี → SourceUnavailable log `SqlErrorNumber 208` ไม่มี key/message; permission denied → `SqlErrorNumber 229`)
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded
       - viewports: n/a — logic-only
       - deviations: integration test อยู่ที่ `tests/Architecture.Tests/EmployeeProfileReaderIntegrationTests.cs` (ไม่ใช่ Integration.Tests) เพราะ `ControlPlaneDbContext` internal และ InternalsVisibleTo มีเฉพาะ Architecture.Tests; fixture ใช้ scratch DB ทิ้งแทน dev DB จึงไม่ต้องคืน `LegacyKey` เป็น NULL; status mapping แยกเป็น pure `EmployeeProfileResolver` + `IEmployeeProfileSource` ใน Admins.Application เพื่อให้ unit test ด้วย fake ได้ (reader ใน Persistence implement ทั้งสอง interface)

- [x] 3. Application resolution ใน transaction เดิม — `ResolveMicrosoftAdminCommand(CanonicalEmail, EmployeeId?,
     CorrelationId)`, `ResolveOutcome` +4 ค่า, `ResolveResult.DenialReason`/`EmployeeConflict`/`HrSourceUnavailable`,
     `EmployeeProfileDeniedException` (throw ใน tx → rollback + clear → catch นอก tx), handler ordering ตาม
     design (outcome เดิมก่อน HR, mismatch, taken ยกเว้นตัวเอง, Inactive same/different, employee-bind audit
     ครั้งเดียว, SaveChanges เดียว), `IUserRepository.GetByEmployeeIdAsync(employeeId, exceptAdminId)`,
     `ConflictException` → re-run 1 ครั้ง (switch เปิด) / recovery เดิม (switch ปิด), แก้ fakes ใน
     Admins.Tests/Hosts.Tests ที่ implement interface เหล่านี้. Done = unit tests ด้วย fake reader/repo/UoW
     ครอบทุก branch + transaction integration test ยืนยัน commit 5 field พร้อมกัน, rollback ไม่เหลือ user/audit,
     unique index race → `employee-taken`, `UpdatedAt` stamp
     Satisfies: REQ-2.5, REQ-2.10, REQ-2.12-REQ-2.14, REQ-2.17, REQ-3.19, REQ-4.11, REQ-5.7, REQ-7.1-REQ-7.9, REQ-7.14-REQ-7.17, REQ-10.2-REQ-10.5, REQ-12.4-REQ-12.6
     Depends on: 1, 2
     Verify: `tests/Admins.Tests/ResolveMicrosoftAdminEmployeeProfileTests.cs`,
     `tests/Integration.Tests/Tier0EmployeeProfileTransactionTests.cs`.
     Evidence:
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build` -> 186 passed / 0 failed (ResolveMicrosoftAdminEmployeeProfileTests 17 ใหม่: JIT+profile 1 commit 2 audit, refresh ไม่มี bind audit ซ้ำ, identical ไม่ bump, mismatch/taken + rollback JIT, 4 reader denial → outcome + rollback, Inactive same/different ×2, Suspended/conflict ก่อน HR, race re-run 1 ครั้ง, race ซ้ำ → employee-taken, switch ปิดไม่แตะ HR + recovery เดิม)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "Category!=Integration"` -> 294 passed / 0 failed (TransactionInventoryTests ยัง 1 call site)
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~Tier0EmployeeProfileTransactionTests|FullyQualifiedName~Tier0WorkforceIdentityMigrationSqlTests"` -> 11 passed / 0 failed (scratch DB: 5 field + JIT + jit-provision/employee-bind audit commit เดียว, refresh Version 2→3 AuthorizationVersion 0 UpdatedAt stamp, identical ไม่ bump; denied → ไม่มี user/audit/subject bind และ scope เดิม save ต่อได้; unique race จริงบน IX_Users_EmployeeId → re-run → `employee-taken` ไม่มี row/audit ค้าง)
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded
       - viewports: n/a — logic-only
       - deviations: transaction integration test อยู่ที่ `tests/Architecture.Tests/Tier0EmployeeProfileTransactionTests.cs` (internal context เหตุผลเดียวกับ task 2); Hosts.Tests ไม่มี fake ของ interface เหล่านี้ (ใช้ `ICallbackResolver`) จึงแก้เฉพาะ `LoginService` ส่ง `EmployeeId: null` ชั่วคราวจนถึง task 4; handler เรียก `SaveChangesAsync` เสมอ (path identity-owner เดิมไม่ save) — no-op เมื่อไม่มี change

- [x] 4. Host: Graph acquisition + switch + callback wiring — `OidcProviderOptions.RequireEmployeeProfile`,
     `AdminAuthOptions.GraphBaseUrl`, named `HttpClient` `microsoft-graph` timeout 10s,
     `MicrosoftGraphEmployeeIdReader` (System.Text.Json, ILogger + correlationId, ห้าม log token/body/exception),
     `EmployeeProfileException` + classifier, `OnTokenValidated` async (scope `User.Read` เมื่อ switch เปิด, Graph
     หลัง gate ก่อน DB, `EmployeeIdPolicy` normalize), `MicrosoftWorkforceClaims.EmployeeId`,
     `ICallbackResolver`/`LoginService` ส่ง employeeId + exhaustive switch ต่อ `ResolveOutcome` + audit
     `DenialReason`, boot guard (Production + switch เปิด + ClientId ว่าง → fail), `Tier0WorkforceArchitectureTests`
     เพิ่มไฟล์ Graph reader/EmployeeProfileReader เข้า gate + `SaveTokens = true` ban + log regex. Done = E2E ผ่าน
     middleware จริงด้วย fake backchannel + fake Graph handler ครอบ success และทุก failure class โดย session store
     ว่างและ denied audit ไม่มี PII; switch ปิด = ไม่มี Graph request และ challenge ไม่มี `User.Read`
     Satisfies: REQ-1.1-REQ-1.23, REQ-9.1-REQ-9.8, REQ-10.1, REQ-10.8-REQ-10.13, REQ-11.1, REQ-11.6, REQ-12.1-REQ-12.3, REQ-12.7-REQ-12.8
     Depends on: 3
     Verify: `tests/Hosts.Tests/AdminGraphEmployeeProfileE2ETests.cs`, `AdminLoginServiceTests.cs` (case ใหม่),
     `ConsoleConfigurationStartupTests.cs` (boot guard), `Tier0WorkforceArchitectureTests`.
     Evidence:
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests"` -> 23 passed / 0 failed (ผ่าน OIDC middleware จริง + fake backchannel + fake Graph handler: switch เปิด scope มี `User.Read`, 200 → resolver ได้ `AB12` normalized + Bearer token ตรง, 400/401/403/404/429/500/502 → `employee-profile-unavailable`, timeout/HttpRequestException → unavailable, JSON พัง → unavailable, ไม่มี/null/ว่าง employeeId → missing, 17 ตัว/whitespace/control/ชนิดผิด → invalid, ไม่มี access token → unavailable ไม่เรียก Graph, workforce gate ล้มก่อน Graph; ทุก denial: resolver ไม่ถูกเรียก, session store ว่าง, denied audit 1 แถวไม่มี token/email/canary; switch ปิด: scope เดิม, ไม่มี Graph request, employeeId null)
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter "...AdminLoginServiceTests|ProvisioningGuardsTests|AdminCallbackResolverInviteBindTests|OidcCallbackE2ETests|MicrosoftAuthLoginRedirectTests"` -> 123 passed (+1 แก้แล้ว) — LoginService map 4 outcome ใหม่ + audit `DenialReason`, enum-coverage guard, employeeId forward, boot guard switch เปิด + ClientId/Secret ว่าง → throw
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "Category!=Integration"` -> 297 passed / 0 failed (Graph reader + EmployeeProfileReader เข้า gate log-exception, `SaveTokens = true` ban, log-PII regex, EmployeeId assignment gate)
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 17 project Passed! / 0 failed
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded
       - viewports: n/a — logic-only (E2E ผ่าน HttpClient ของ WebApplicationFactory)
       - deviations: boot guard REQ-12.8 ไม่เพิ่มโค้ด — `RequireWorkforceAdminProvider` เดิมล้ม boot อยู่แล้วเมื่อ provider ว่าง (test ใหม่ใน `ProvisioningGuardsTests` ไม่ใช่ `ConsoleConfigurationStartupTests`); switch expression ต้องมี discard arm (CS8524) จึงใช้ `_ => throw UnreachableException` (fail closed) + test enumerate enum แทน compile error; `EmployeeProfileException.Reason` ใช้ค่า string ตรงเป็น browser reason

- [x] 5. Config, runbook และ repo gates — `.env.example` (2 key ใหม่ placeholder), `docker-compose.prod.yml`
     passthrough `ADMIN_REQUIRE_EMPLOYEE_PROFILE` default `false` + placeholder ใน render-check ทั้ง 2 CI,
     runbook `docs/runbooks/admin-microsoft-oidc.md`: grant `User.Read` + admin consent, เตรียม `LegacyKey`
     (SQL template ไม่มีค่าจริง), deployment order (migration → mapping → consent → เปิด switch), rollback
     (ปิด switch ก่อน, `Down()` ทีหลัง), script ปลด `EmployeeId` ที่ user รันเอง; รัน secret scan + PII log scan
     ของ repo และ `scripts/spec-trace.sh tier0-graph-employee-profile`. Done = ทุก gate เขียว, ไม่มี employeeId/
     email/ชื่อจริงใน fixture หรือเอกสาร
     Satisfies: REQ-9.9-REQ-9.10, REQ-11.2-REQ-11.5, REQ-11.8
     Depends on: 4
     Verify: `.ai/bin/check-secrets.sh`, repo PII scan gate, `scripts/spec-trace.sh tier0-graph-employee-profile`,
     `Tier0WorkforceArchitectureTests.Production_compose_configures_admin_microsoft_without_retired_google_provider`.
     Evidence:
       - test: `.ai/bin/check-secrets.sh --all` -> EXIT=0 (ไม่มี finding)
       - test: `scripts/spec-trace.sh tier0-graph-employee-profile` -> OK: เกณฑ์ 167 ข้อ ถูกอ้างครบ, EARS lint ผ่าน
       - test: `scripts/check-migration-script.sh` -> Schema script drift gate OK
       - test: `docker compose -f docker-compose.prod.yml config` (placeholder env เดียวกับ CI + `ADMIN_REQUIRE_EMPLOYEE_PROFILE=false`) -> render ผ่าน, `AdminAuth__Providers__Microsoft__RequireEmployeeProfile: "false"`
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"` (รวม `Production_compose_configures_admin_microsoft_without_retired_google_provider`) -> 297 passed / 0 failed ทั้ง project
       - PII scan: grep email/ตัวเลข 5-8 หลักในไฟล์ที่เปลี่ยน -> พบเฉพาะ fixture สังเคราะห์เดิม (`ops@org.com`, `no-reply@example.test`, `*@viriyah.co.th` ปลอม); employeeId ใน test ใช้ prefix `ZTEST-`/`AB12`/`E001` เท่านั้น
       - docs: `docs/runbooks/admin-microsoft-oidc.md` ใหม่ (consent `User.Read`, SQL template `LegacyKey` ไม่มีค่าจริง, deployment order migration → mapping → consent → switch, rollback ปิด switch ก่อน `Down()`, script ปลด `EmployeeId` ให้ user รันเอง, failure map); `.env.example` +2 key, `docker-compose.prod.yml` passthrough, placeholder ใน `.github/workflows/ci.yml` + `.gitlab-ci.yml`
       - viewports: n/a — docs/config-only
       - deviations: repo ไม่มี "PII log scan gate" แยกจาก `check-secrets.sh` — ใช้ secret scan + grep มือ + `Tier0WorkforceArchitectureTests` log-PII regex แทน

## Suggested execution batches

> DEFAULT for a COUPLED feature (tasks share primitives/data/lib): run ALL tasks in
> ONE session — `scripts/pane-loop.sh tier0-graph-employee-profile all-in-one` (or `/spec-implement all`).
> Separate sessions do NOT share cache, so each one re-pays the cold cache-write to
> re-acquire shared context — measured ~30-40% more expensive for coupled work.
> Split into separate sessions/panes ONLY for accuracy: a genuinely INDEPENDENT task
> (no shared state), or to isolate a CORE domain (e.g. pricing logic) from long-context
> drift — a conscious accuracy trade, not a cost win.

- task 1-4 coupled (share `EmployeeIdPolicy`, port types, `ResolveOutcome`) → one session ตามลำดับ 1 → 2 → 3 → 4
- task 5 เป็น docs/config fast lane รันต่อท้ายใน session เดียวกันได้ ไม่ต้อง batch tag
- ห้ามรัน task 2 กับ 3 ขนานกัน (แก้ interface เดียวกัน)

## Environment constraints

- `dotnet build/test` ต้องเป็นคำสั่งเดี่ยวให้ `excludedCommands` จับ; build ใน sandbox ไม่เขียน dll (เช็ค mtime)
- integration ใช้ `pol-db` :11433 `VCentralPay`; `cfg.VibEmp`/`cfg.branch` บน dev มี PII จริง — ห้าม dump, assert เฉพาะ row ตัวเอง
- หลัง `ef migrations add` ต้อง `scripts/check-migration-script.sh --write` (schema script 1 batch ต่อ command)
- ห้าม commit/push จนกว่าผู้ใช้สั่ง

## Known failures

- `dotnet build`/`dotnet ef`/`check-migration-script.sh` ใน Claude sandbox: restore ค้าง ~5 นาทีแล้วจบ `Build FAILED. 0 Error(s)` โดยไม่เขียน dll — ต้องรันคำสั่งนั้นนอก sandbox (คำสั่งเดี่ยว); `dotnet test --no-build` ใน sandbox ใช้ได้
- `docker/bootstrap/assert-fresh-db.test.sh` ใน sandbox: `mktemp ... Operation not permitted` (ใช้ /var/folders) — รันนอก sandbox
- SQL Server เก็บ `filter_definition` ของ filtered index แบบมีวงเล็บ `([EmployeeId] IS NOT NULL)` — assert ด้วย Contains ไม่ใช่ Equal
- `InitialSchema` ปฏิเสธ target DB ที่ไม่ว่าง — fixture ที่ต้องมี HR tables ก่อน `Tier0EmployeeProfile` ต้อง migrate ถึง `Tier0WorkforceEmailIdentity` ก่อน สร้างตาราง แล้ว migrate ต่อ
- C# switch expression บน enum ต้องมี discard arm เสมอ (CS8524 นับ unnamed value) — exhaustiveness ของ `ResolveOutcome` บังคับด้วย `AdminLoginServiceTests.Every_resolve_outcome_is_mapped_to_a_browser_reason` แทน compiler
- JSON body ที่มี control character ดิบ = JSON พัง (→ `unavailable`) ไม่ใช่ `invalid`; ทดสอบ REQ-2.3 ผ่าน Graph ต้องใช้ `` escape
