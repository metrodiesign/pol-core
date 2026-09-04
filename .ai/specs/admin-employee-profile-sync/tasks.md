# Implementation Tasks: Admin Employee Profile Sync

> Status: approved 2026-09-03
> Status-Note: amended and approved 2026-09-03 — Task 4 mandatory Graph delta

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

Gate ต่อ task: `dotnet build pol-core.slnx --no-restore -warnaserror` และ
`dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` งานที่แตะ SQL Serverต้องรัน targeted integration testจริงด้วย Taskสุดท้ายรัน required commandทั้งชุดจาก requirements

- [x] 1. **Exact HR profile sync transaction** — ลด employee profile contractเหลือ `EmpCode`/ชื่อ, เปลี่ยน production readerเป็น exact parameterized `dbo.VibEmp` queryเดียว, ลด aggregate writerเหลือ `EmployeeId`/ชื่อ, retire branch/Office/Division/unmapped path, เพิ่ม `employee-profile-sync` audit และ wire existing/JIT bind-refresh-no-op-mismatch-taken/raceทั้งหมดใน identity transactionเดิม Doneเมื่อ unit testsและ real SQL reader/transaction integration testsพิสูจน์ cardinality, name validation, field/version preservation, audit matrix, rollbackและ duplicate raceครบ
     Satisfies: REQ-2.1-REQ-2.7, REQ-3.1-REQ-3.14, REQ-4.1-REQ-4.9, REQ-5.1-REQ-5.23, REQ-6.1-REQ-6.13, REQ-6.16-REQ-6.19, REQ-9.2-REQ-9.9, REQ-9.11
     Verify: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --filter "FullyQualifiedName~EmployeeIdPolicyTests|FullyQualifiedName~EmployeeProfileReaderStatusTests|FullyQualifiedName~UserEmployeeProfileTests|FullyQualifiedName~ResolveMicrosoftAdminEmployeeProfileTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~EmployeeProfileReaderIntegrationTests|FullyQualifiedName~Tier0EmployeeProfileTransactionTests"`; standard task gate
     Evidence:
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> Build succeeded, 0 warnings, 0 errors
       - test: `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-build --filter "FullyQualifiedName~EmployeeIdPolicyTests|FullyQualifiedName~EmployeeProfileReaderStatusTests|FullyQualifiedName~UserEmployeeProfileTests|FullyQualifiedName~ResolveMicrosoftAdminEmployeeProfileTests"` -> 43 passed / 0 failed
       - test: `set -a; source .env.integration; set +a; dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~EmployeeProfileReaderIntegrationTests|FullyQualifiedName~Tier0EmployeeProfileTransactionTests"` -> 9 passed / 0 failed; exact SQL, 0/1/2 rows, invalid names, SQL -2/208/229, cancellation, JIT/refresh/no-op/raceผ่าน
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 2,075 passed / 0 failed
       - viewports: n/a — logic/backend-only
       - deviations: `LoginService`และ Host testลบ retired unmapped enum referenceใน Task 1เพื่อให้ solution compile; Host behavior verificationอยู่ Task 2ตามแผน

- [x] 2. **Validated Graph, denial and session boundary** — pin Production Graph origin, preserve one transient no-retry Graph requestหลัง validated `tid+oid`, update final outcome mapping, prove Graph/profile failures create no resolver mutationหรือ session, and harden denied-auth/logging paths against token/PII/SQL detail Doneเมื่อ real OIDC middleware E2E, LoginService outcome tests, Production guardและ static security gatesผ่าน
     Satisfies: REQ-1.1-REQ-1.23, REQ-2.8-REQ-2.9, REQ-3.15-REQ-3.16, REQ-6.14-REQ-6.15, REQ-6.20, REQ-7.1-REQ-7.15, REQ-9.1, REQ-9.10, REQ-9.12-REQ-9.13
     Depends on: 1
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~ProvisioningGuardsTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"`; standard task gate
     Evidence:
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> Build succeeded, 0 warnings, 0 errors
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~ProvisioningGuardsTests"` -> 104 passed / 0 failed; OIDC middleware, request count, literal denial, empty session/audit safetyและ Production Graph pinผ่าน
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"` -> 19 passed / 0 failed; exact three-column path, no legacy mapping producer, audit literalและ PII-safe logger gatesผ่าน
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 2,079 passed / 0 failed
       - viewports: n/a — backend-only
       - deviations: final outcome mappingถูกลบใน Task 1เพื่อรักษา compile boundary; Task 2เพิ่ม Production Graph origin guardและขยาย Host/static evidenceตาม design

- [x] 3. **Schema, least-privilege operations and final assembly** — ยืนยันโดยไม่สร้าง migrationใหม่ว่า HEADคง `nvarchar(16/500/500)`กับ global filtered unique EmployeeId index, VibEmpไม่เป็น EF entity/write path, fresh DBผ่านเมื่อ sourceไม่มี, conditional/late operator grantให้ `pol_app`เฉพาะ `SELECT`, และปรับ runbookให้เหลือ flowสาม fieldโดยไม่มี branch/LegacyKey instructions Doneเมื่อ migration/permission/architecture assertions, docs, full build/test/integration, migration drift, secret scanและ spec traceผ่านจริง
     Satisfies: REQ-3.12-REQ-3.13, REQ-7.12, REQ-8.1-REQ-8.14, REQ-9.14-REQ-9.15
     Depends on: 1, 2
     Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Tier0EmployeeProfileMigrationTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~EmployeeProfileReaderIntegrationTests|FullyQualifiedName~ModelDisjointnessTests|FullyQualifiedName~BypassPrimitiveTests|FullyQualifiedName~Tier0WorkforceArchitectureTests"`; required commandทั้งชุดใน `design.md`
     Evidence:
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> Build succeeded, 0 warnings, 0 errors
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 2,079 passed / 0 failed
       - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> Integration.Tests 179 passed + Architecture.Tests 17 passed, รวม 196 passed / 0 failed
       - test: `set -a; source .env.integration; set +a; dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter "FullyQualifiedName~Tier0EmployeeProfileMigrationTests"` -> 2 passed / 0 failed
       - test: `set -a; source .env.integration; set +a; dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~EmployeeProfileReaderIntegrationTests|FullyQualifiedName~ModelDisjointnessTests|FullyQualifiedName~BypassPrimitiveTests|FullyQualifiedName~Tier0WorkforceArchitectureTests"` -> 26 passed / 0 failed
       - script: `scripts/check-migration-script.sh` -> Schema script drift gate OK
       - script: `.ai/bin/check-secrets.sh --all` -> exit 0, no findings
       - script: `scripts/spec-trace.sh admin-employee-profile-sync` -> 144 criteria covered, EARS lint passed
       - check: `git diff --check` -> exit 0
       - viewports: n/a — backend/docs-only
       - deviations: ไม่มี migrationใหม่ตาม approved design; targeted Architecture commandรอบแรกไม่ได้ load `.env.integration`จึง fail 3 testsด้วย missing `POL_SA_PASSWORD`, rerunคำสั่งเดียวกันหลัง load envแล้ว 26/26ผ่าน

- [x] 4. **Mandatory Graph on every new Admin Microsoft callback** — ลบ `RequireEmployeeProfile`และ config default-falseทุกจุด, ขอ `User.Read`กับเรียก Graphแบบ unconditionalหลัง validated `tid+oid`, classify exact `consent_required`โดยไม่ parse provider detail, preserve user-cancel `access_denied`, และพิสูจน์ existing session/rotationไม่เรียก Graph Doneเมื่อ OIDC middleware E2Eครอบ scope, one call, missing token, 401/403, consent, cancel, session isolation, no mutation/sessionและ Merchant auth regression พร้อม full non-integration/spec-trace/diff gates
     Satisfies: REQ-1.5-REQ-1.6, REQ-1.16, REQ-1.19, REQ-1.22-REQ-1.31, REQ-7.16-REQ-7.17, REQ-9.16-REQ-9.20
     Depends on: 1, 2, 3
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~AdminSessionAuthHandlerTests|FullyQualifiedName~MicrosoftAuthLoginRedirectTests"`; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"`; `dotnet build pol-core.slnx --no-restore -warnaserror`; `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`; `scripts/spec-trace.sh admin-employee-profile-sync`; `git diff --check`
     Evidence:
       - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> Build succeeded, 0 warnings, 0 errors
       - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~AdminSessionAuthHandlerTests|FullyQualifiedName~MicrosoftAuthLoginRedirectTests"` -> 70 passed / 0 failed; mandatory scope/Graph, missing token, 401/403, exact consent, user cancel, existing session rotationและ Merchant scope regressionผ่าน
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"` -> 20 passed / 0 failed; no runtime switch, one OIDC Graph call siteและ no session Graph dependencyผ่าน
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 2,080 passed / 0 failed
       - script: `scripts/spec-trace.sh admin-employee-profile-sync` -> 159 criteria covered, EARS lint passed
       - script: `.ai/bin/check-secrets.sh --all` -> exit 0, no findings
       - script: `scripts/check-migration-script.sh` -> Schema script drift gate OK
       - check: `git diff --check` -> exit 0
       - viewports: n/a — backend/config/docs-only
       - deviations: ไม่รัน SQL integrationซ้ำเพราะ Task 4ไม่แตะ reader, transaction, schemaหรือ migration; focused/full testรอบแรกพบ fixtureและ expected scopeเดิม จากนั้นปรับเฉพาะ testsที่ต้องรองรับ mandatory Graphแล้ว rerunเขียว

## Suggested execution batches

Tasks 1–4เสร็จพร้อม Evidenceแล้ว ไม่มี implementation taskค้าง Task 4 amendmentแตะเฉพาะ Admin OIDC/config/tests/docsและไม่ได้เปลี่ยน HR reader, profile transaction, migration, schemaหรือ Merchant authentication
