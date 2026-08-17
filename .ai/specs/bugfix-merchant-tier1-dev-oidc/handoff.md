# Handoff: Merchant Tier 1 Local DEV OIDC Configuration

> From: Codex root session   To: any   Date: 2026-08-17

## Task Summary

แก้ Merchant Tier 1 local login ให้ใช้ `VCP External DEV` ตาม bugfix task 1 ครอบ F1-F7 และ B1-B8 ใน `bugfix.md`

## Current Status

Implementation และ verification เสร็จแล้ว. Live Tier 1 login แลก authorization code สำเร็จและ redirect ไป `https://localhost:3002/register?ticket=<redacted>` แทน `auth-failed`

Task 1 ปิดครบด้วย automated test gate จาก terminal ที่อนุญาต local sockets

## Files Changed

- `.ai/specs/bugfix-merchant-tier1-dev-oidc/bugfix.md` — สร้างและอนุมัติ F1-F7/B1-B8
- `.ai/specs/bugfix-merchant-tier1-dev-oidc/tasks.md` — approved task 1 พร้อม final evidence
- `.ai/specs/bugfix-merchant-tier1-dev-oidc/handoff.md` — บันทึกสถานะส่งมอบ
- `src/Hosts/Api/Properties/launchSettings.json` — ใช้ HTTPS `5001`, tenant/app ใหม่ และ canonical callback โดยไม่มี secret
- `tests/Architecture.Tests/LocalDevelopmentOriginTests.cs` — เปลี่ยน regression oracle เป็น config ใหม่และ assert ว่า launch profile ไม่มี client secret
- `tests/Hosts.Tests/OidcCallbackE2ETests.cs` — เปลี่ยน CIAM fixture เป็น tenant ใหม่และตรวจ generated HTTPS authorize request

## Important Decisions

- ใช้ Authority แบบ tenant-pinned เพื่อให้ issuer validation ปฏิเสธ tenant อื่น แม้ Entra application รองรับหลายองค์กร
- เก็บ Client ID และ Authority ใน launch environment เพราะเป็น public identifier แต่เก็บ client secret นอก tracked files เท่านั้น
- ไม่ใช้ secret ที่ผู้ใช้ส่งในแชต เพราะถือว่ารั่วแล้ว ต้อง revoke และสร้างใหม่
- ไม่เปลี่ยน Admin Tier 0, Google, callback session branching หรือ error mapping
- Root cause คือ runtime client secret ใช้ไม่ได้: Entra ตอบ `AADSTS7000215` ระหว่าง token redemption ไม่ใช่ redirect URI, PKCE, state, nonce, issuer หรือ user flow
- หลัง operator ใส่ replacement secret นอก Git และ restart API เส้นทางเดิมสำเร็จโดยไม่ต้องแก้ authentication handler

## Constraints

- ห้ามบันทึกหรือ log client secret, token หรือ OTP
- ห้ามอ่านหรือแก้ real `appsettings.Development.json` ผ่าน agent; operator จัดการ local secret source เอง
- ห้าม push ตรง `main` หรือ `develop`; ห้าม commit ก่อน review
- worktree มี user changes จำนวนมาก ต้อง preserve และห้าม revert

## Tests Run

- `jq -e '<local OIDC assertions>' src/Hosts/Api/Properties/launchSettings.json` -> `true`
- `jq -e '<local origin assertions>' src/Hosts/Api/appsettings.Development.json.example` -> `true`
- `dotnet build tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --nologo -m:1 -nr:false /p:UseSharedCompilation=false` -> succeeded, 0 warnings, 0 errors
- `dotnet build tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --nologo -m:1 -nr:false /p:UseSharedCompilation=false` -> succeeded, 0 warnings, 0 errors
- Targeted Hosts regression -> passed 2/2
- `dotnet build pol-core.slnx --no-restore --nologo -m:1 -nr:false /p:UseSharedCompilation=false` -> succeeded, 0 warnings, 0 errors
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration" --nologo -m:1 -nr:false` -> passed 1756/1756, 0 failed, 0 skipped
- `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter "FullyQualifiedName~ProviderDiscriminatorMigrationTests" --nologo` -> passed 3/3
- `git diff --check -- <tracked task files>` และ untracked-file whitespace guard -> exit 0
- `scripts/spec-trace.sh bugfix-merchant-tier1-dev-oidc` -> exit 0; tool ระบุ bugfix spec แล้วข้าม requirements trace
- `.ai/bin/check-secrets.sh --all` -> exit 0
- Live browser login ผ่าน tenant `VCP External DEV`, code exchange และ identity validation แล้ว redirect ไป Merchant registration outcome พร้อม redacted ticket

## Known Issues

- Client secret เก่าที่เคยส่งในแชตต้อง revoke หากยังไม่ได้ revoke; replacement ห้ามส่งผ่านแชตหรือบันทึกใน tracked files
- Managed sandbox ยังเปิด VSTest socket ไม่ได้; final automated evidence มาจาก terminal ปกติและผ่านทั้งหมด
- Merchant SPA ต้องทำงานที่ `https://localhost:3002` เพื่อ render หน้า `/register`; backend redirect outcome ถูกต้องแล้ว

## Next Steps

1. Revoke client secret เก่าที่เปิดเผย หากยังไม่ได้ทำ
2. เก็บ replacement secret ใน environment, user-secrets หรือ secret manager เท่านั้น
