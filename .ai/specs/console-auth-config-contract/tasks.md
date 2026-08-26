# Implementation Tasks: Console Auth Configuration Contract

> Status: approved 2026-08-18

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass; decompose micro-steps only during execution.

Scope guard: ไม่เปลี่ยน OIDC route/callback/scheme, cookie contract, REST/OpenAPI, database, UI หรือไฟล์ `.env`

- [x] 1. สร้าง canonical configuration snapshot และ compatibility resolver — bind canonical Admin/Merchant session กับ CORS, merge legacy aliases ราย field ตาม provider precedence, normalize และตรวจ conflict ด้วย unit tests ครบถ้วน
    Satisfies: REQ-1 (all criteria), REQ-2.1-REQ-2.2, REQ-2.4-REQ-2.8, REQ-2.10-REQ-2.14, REQ-8.1, REQ-8.3. Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationResolverTests`.

    Evidence:
      - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationResolverTests` -> 6 passed, 0 failed, 0 skipped
      - viewports: n/a — logic-only
      - deviations: none

- [x] 2. บังคับ startup validation และ deprecation reporting — wire snapshot เข้า API host แบบ lazy-until-startup, fail ก่อนรับ request, log key-family warning ครั้งเดียว และทดสอบ validation matrix กับ provider stack จริง
    Satisfies: REQ-2.3, REQ-2.7, REQ-2.9, REQ-3 (all criteria), REQ-8.2, REQ-8.4-REQ-8.6. Depends on: 1. Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationStartupTests`.

    Evidence:
      - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationStartupTests` -> 31 passed, 0 failed, 0 skipped
      - viewports: n/a — startup/configuration logic only
      - deviations: none

- [x] 3. ย้าย runtime consumers ไป canonical snapshot โดยคง auth behavior — update Admin/Merchant redirect, registration, invitation และ typed CORS policies พร้อม regression tests สำหรับ allowlist, Scalar, plane isolation, credentials, callbacks, schemes และ cookies เดิม
    Satisfies: REQ-4 (all criteria), REQ-5 (all criteria), REQ-6 (all criteria), REQ-8.7-REQ-8.8. Depends on: 1, 2. Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~MerchantUserLoginServiceTests|FullyQualifiedName~CorsTests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~Invitation"`.

    Evidence:
      - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~MerchantUserLoginServiceTests|FullyQualifiedName~CorsTests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~Invitation"` -> 52 passed, 0 failed, 0 skipped
      - viewports: n/a — API auth/CORS behavior only
      - deviations: none

- [x] 4. ย้าย tracked configuration และ operator documentation — ใช้ canonical keys ใน appsettings examples, launch settings, Compose, `.env.example` และ current docs โดยคง Compose input names, migration map, secret rules และ historical specs พร้อม contract-pin tests
    Satisfies: REQ-7 (all criteria), REQ-8.9. Depends on: 1. Verify: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter FullyQualifiedName~LocalDevelopmentOriginTests`.

    Evidence:
      - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter FullyQualifiedName~LocalDevelopmentOriginTests` -> 1 passed, 0 failed, 0 skipped
      - viewports: n/a — tracked configuration and documentation only
      - deviations: none

- [x] 5. ประกอบและพิสูจน์ release gate — รัน canonical/legacy regression, build warnings-as-errors, non-integration และ SQL integration suites, rename contract check, secret scan และ spec trace จนครบโดยไม่มี uncovered REQ
    Satisfies: REQ-8.10. Depends on: 1, 2, 3, 4. Verify: commands ใน Completion gate ด้านล่างผ่านทั้งหมดและบันทึกผลจริงใน Evidence.

    Evidence:
      - restore: `dotnet restore pol-core.slnx` -> passed
      - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> passed, 0 warnings, 0 errors
      - non-integration: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,796 passed, 0 failed, 0 skipped
      - SQL integration: human-run output for the solution-wide `Category=Integration` filter -> 150 passed, 0 failed, 0 skipped, 17.6s
      - SQL integration warnings: 16 non-integration test assemblies reported `No test matches the given testcase filter`; `Integration.Tests` passed without a warning
      - secret scan: `.ai/bin/check-secrets.sh --all` -> passed
      - spec trace: `scripts/spec-trace.sh console-auth-config-contract` -> 91/91 criteria referenced; EARS lint passed
      - rename contract: no legacy key literal remains in tracked runtime config/current reference files; compatibility resolver, migration map and compatibility tests retain legacy literals intentionally
      - diff: `git diff --check` -> passed
      - environment: local macOS workspace, .NET SDK 10.0.300 and test runtime 10.0.8; SQL run executed in the human operator's local shell, and no secret value was supplied to or read by Codex
      - deviations: SQL integration evidence was supplied by the human operator because the Codex process did not receive local database credentials

## Suggested execution batches

งาน coupled: ทุก task แชร์ resolver, options และ host composition root ให้ใช้ session เดียวตามค่า default:

```bash
scripts/pane-loop.sh console-auth-config-contract all-in-one
```

หรือ invoke `$spec-implement all`; ไม่มี `Batch:` แยก เพราะการแยก context เพิ่มความเสี่ยงให้ contract และ tests ไม่ตรง snapshot เดียวกัน

## Completion gate

```bash
dotnet restore pol-core.slnx
dotnet build pol-core.slnx --no-restore -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test pol-core.slnx --no-build --filter "Category=Integration"
.ai/bin/check-secrets.sh --all
scripts/spec-trace.sh console-auth-config-contract
git diff --check
```

Task 5 ต้องบันทึกจำนวน pass/fail จริง, environment ของ SQL integration และ deviation ทุกข้อใน `Evidence:`
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
