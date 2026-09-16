# Tasks: Agent CIAM account switch และ end-session

> Status: approved 2026-09-16

## Implementation Plan

### Goal

เพิ่ม Agent-only prompt forwarding, server-owned CIAM end-session contract และ Agent Bearer logout regression coverage ตาม F-1 ถึง F-8 โดยรักษา B-1 ถึง B-5

### Affected files

- `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs` — edit — validate/forward Agent prompt และเพิ่ม browser end-session endpoint
- `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs` — edit — validate Agent web origin ตามสถานะ provider
- `src/Api/Api/IdentityAccess/IdentityAccessOptions.cs` — edit — ทำ origin validation ให้ strict และรองรับ required Agent origin
- `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs` — edit — ประกาศ backend end-session endpoint ใน OAuth metadata
- `tests/IntegrationTests/Hosts.Tests/AgentLoginTests.cs` — edit — พิสูจน์ prompt, end-session, isolation และ Agent token revocation
- `tests/IntegrationTests/Hosts.Tests/IdentityAccessLoginTests.cs` — edit — พิสูจน์ Employee prompt behavior ไม่เปลี่ยน
- `tests/IntegrationTests/Hosts.Tests/IdentityAccessRedirectTests.cs` — edit — พิสูจน์ config validation
- `tests/IntegrationTests/Hosts.Tests/Task8IdentityAccessA1Tests.cs` — edit — pin discovery contract
- `docs/runbooks/agent-ciam-oauth.md` — create — ระบุ API/config/Entra contract โดยไม่ hardcode tenant URL

### Steps

1. เพิ่ม regression tests และเก็บผล RED ของ prompt/end-session ก่อนแก้ production code
2. เพิ่ม allowlist forwarding และ fixed end-session redirect จาก OIDC metadata
3. เพิ่ม config validation, runbook, build/test/spec-trace และ security review

### Risks / open questions

- `end_session_endpoint` เป็น external metadata จึงต้องตรวจเป็น absolute HTTPS URI และ fail closed เมื่อไม่มีหรือไม่ปลอดภัย
- การเพิ่ม Entra post-logout URI เป็น external operation; งานนี้บันทึก URI เท่านั้นและไม่เปลี่ยน app registration

## งาน

- [x] 1. เพิ่ม regression tests สำหรับ Agent prompt, realm isolation และ end-session contract
  Satisfies: F-1, F-2, F-3, F-4, F-5, F-7, B-1, B-2, B-3, B-5
  Verify: รัน targeted `AgentLoginTests` และเก็บผล RED ก่อนแก้ จากนั้นต้อง GREEN หลังแก้

  Evidence:

  - test: `set -a; source .env.integration; set +a; dotnet test tests/IntegrationTests/IntegrationTests.csproj --filter "FullyQualifiedName~Hosts.Tests.AgentLoginTests" --no-restore` ก่อนแก้ production code -> RED 4, ผ่าน 3; failure mode คือ prompt หาย, prompt อื่นได้ `302` และ end-session route ได้ `404`
  - test: `set -a; source .env.integration; set +a; dotnet test tests/IntegrationTests/IntegrationTests.csproj --filter "FullyQualifiedName~Hosts.Tests.AgentLoginTests|FullyQualifiedName~Hosts.Tests.Task8IdentityAccessA1Tests.A1_Discovery" --no-restore` -> ผ่าน 8, ไม่ผ่าน 0
  - viewports: n/a — logic-only
  - deviations: ไม่มี

- [x] 2. เพิ่ม Agent prompt forwarding, CIAM end-session endpoint และ Agent Bearer revocation coverage
  Satisfies: F-1, F-2, F-3, F-4, F-5, F-6, F-7, B-1, B-2, B-4, B-5
  Verify: targeted host integration tests ผ่านและ token เดิมเรียก `/api/v1/me` ได้ `401`

  Evidence:

  - test: `set -a; source .env.integration; set +a; dotnet test tests/IntegrationTests/IntegrationTests.csproj --filter "FullyQualifiedName~Hosts.Tests.AgentLoginTests|FullyQualifiedName~Hosts.Tests.IdentityAccessLoginTests|FullyQualifiedName~Hosts.Tests.IdentityAccessRedirectTests|FullyQualifiedName~Hosts.Tests.IdentityAccessTokenFlowTests|FullyQualifiedName~Hosts.Tests.Task8IdentityAccessA1Tests.A1_Discovery" --no-restore` -> ผ่าน 34, ไม่ผ่าน 0
  - assertion: Agent flow ยืนยัน `POST /api/v1/auth/logout` ได้ `204` และ token เดิมเรียก `/api/v1/me` ได้ `401`
  - viewports: n/a — logic-only
  - deviations: ไม่มี

- [x] 3. ปิด config/runbook/security gates และ full relevant suite
  Satisfies: F-8, B-2, B-3, B-5
  Verify: config tests, `dotnet build`, full `Hosts.Tests`, spec trace และ secret scan ผ่าน

  Evidence:

  - build: `dotnet build pol-core.slnx --no-restore -warnaserror` -> สำเร็จ 0 warnings, 0 errors
  - test: `set -a; source .env.integration; set +a; dotnet test tests/IntegrationTests/IntegrationTests.csproj --no-build --filter "Capability=IdentityAccess"` -> รอบยืนยันสุดท้ายผ่าน 58, ไม่ผ่าน 0
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> ผ่าน Unit 940, Architecture 315, Hosts 631; ไม่ผ่าน 0
  - security: `.ai/bin/check-secrets.sh --all` -> ผ่านโดยไม่มี output
  - trace: `python3 scripts/spec_contract.py check --all --strict` -> strict trace ตรวจ F-1 ถึง F-8 และ B-1 ถึง B-5 ครบหลังแก้รูปแบบ canonical
  - viewports: n/a — logic-only
  - deviations: รอบก่อนหน้าของ IdentityAccess suite พบ startup race เดิมที่ `EmployeeTokenFlow.EnsureClientAsync` จำนวน 3 tests แล้ว rerun คำสั่งเดิมผ่าน 58/58; `dotnet format pol-core.slnx --verify-no-changes --no-restore` ยังแดงจาก whitespace เดิมในไฟล์นอก scopeหลายไฟล์ และ targeted check พบ legacy whitespace ท้าย `IdentityAccessEndpoints.cs` กับ `Task8IdentityAccessA1Tests.cs` ที่ไม่ได้เปลี่ยนใน diff จึงไม่ reformat งานอื่น
