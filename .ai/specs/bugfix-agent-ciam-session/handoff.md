# Handoff Note: Agent CIAM account switch และ end-session

> From: Pi session   To: human reviewer   Date: 2026-09-16

## Task Summary

แก้ spec `bugfix-agent-ciam-session` ครบ F-1 ถึง F-8 และ B-1 ถึง B-5 เพิ่ม Agent-only prompt forwarding, platform metadata, fixed CIAM logout redirect และ Agent Bearer revocation regression test

## Current Status

งาน source, tests, config validation และ runbook เสร็จแล้วบน branch `fix/agent-ciam-account-switch-logout` ยังไม่มี commit, push หรือ PR

## Files Changed

- `.ai/specs/bugfix-agent-ciam-session/bugfix.md` — root cause และ F/B contract (new)
- `.ai/specs/bugfix-agent-ciam-session/tasks.md` — plan และ Evidence (new)
- `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs` — prompt allowlist และ Agent end-session route (edited)
- `src/Api/Api/IdentityAccess/IdentityAccessOptions.cs` — Agent origin validation (edited)
- `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs` — Agent provider-aware validation และ scheme constant (edited)
- `src/Infrastructure/Persistence/Persistence.ControlPlane/OpenIddictRegistration.cs` — OAuth metadata `end_session_endpoint` (edited)
- `tests/IntegrationTests/Hosts.Tests/AgentLoginTests.cs` — Agent prompt/logout/end-session tests (edited)
- `tests/IntegrationTests/Hosts.Tests/IdentityAccessLoginTests.cs` — Employee isolation test (edited)
- `tests/IntegrationTests/Hosts.Tests/IdentityAccessRedirectTests.cs` — config tests (edited)
- `tests/IntegrationTests/Hosts.Tests/Task8IdentityAccessA1Tests.cs` — discovery contract test (edited)
- `docs/runbooks/agent-ciam-oauth.md` — consumer และ Entra runbook (new)

## Important Decisions

- รับเฉพาะ Agent prompt ค่า `select_account`; Agent prompt ค่าอื่นตอบ OAuth `invalid_request`
- Browser logout อ่าน CIAM endpoint จาก OIDC configuration และสร้าง post-logout URI จาก `AgentWebAppBaseUrl` บวก `/login`
- Backend endpoint ถูกประกาศผ่าน OAuth metadata เพื่อ consumer ไม่ต้อง hardcode CIAM authority
- ไม่ส่ง `id_token_hint` หรือ token ใดใน logout URL

## Constraints

- ห้ามแก้ Entra app registration ก่อน human authorization
- ห้าม hardcode tenant URL หรือรับ post-logout URL จาก browser
- ห้ามเปลี่ยน Employee/Admin และ legacy MerchantUser behavior
- ห้าม push ตรง `main` หรือ `develop`

## Tests Run

- `dotnet build pol-core.slnx --no-restore -warnaserror` -> สำเร็จ 0 warnings, 0 errors
- `set -a; source .env.integration; set +a; dotnet test tests/IntegrationTests/IntegrationTests.csproj --no-build --filter "Capability=IdentityAccess"` -> รอบยืนยันสุดท้ายผ่าน 58
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> ผ่าน Unit 940, Architecture 315, Hosts 631
- `.ai/bin/check-secrets.sh --all` -> ผ่าน

## Known Issues

- IdentityAccess suite มี startup race เดิมใน `EmployeeTokenFlow.EnsureClientAsync`; rerun คำสั่งเดิมผ่าน 58/58
- `dotnet format --verify-no-changes` ทั้ง solution แดงจาก whitespace baseline นอก scope; ไม่แก้ไฟล์ unrelated
- การแก้ Entra post-logout redirect URI และ live CIAM browser verification ยังเป็น external blocker

## Next Recommended Agent

ให้มนุษย์ review security/API contract แล้ว operator ที่ได้รับ authorization ตั้ง Entra post-logout URI

## Next Steps

1. Review diff และ runbook `docs/runbooks/agent-ciam-oauth.md`
2. เพิ่ม `https://localhost:3002/login` ใน Entra app registration หลังได้รับ authorization
3. ให้ทีม `pol-merchant` ทำ browser verification ตาม runbook แล้วจึง commit/push/open PR ตามคำสั่งแยก
