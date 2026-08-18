# Handoff: Console Auth Configuration Contract

> From: Codex `/root`
> To: any
> Date: 2026-08-18

## Task Summary

Implement spec `.ai/specs/console-auth-config-contract` สำหรับ canonical Admin/Merchant session และ CORS
configuration, one-release legacy aliases, startup validation, runtime consumers และ tracked deployment artifacts.
ครอบคลุม `REQ-1` ถึง `REQ-8`.

## Current Status

- Tasks 1-5 complete พร้อม Evidence ใน `tasks.md`.
- SQL integration ผ่าน 150/150 จาก output ที่ human operator ส่งให้.
- Release gate ครบ: restore, warnings-as-errors build, non-integration, SQL integration, rename contract,
  secret scan, spec trace และ diff check.
- ไม่มี commit หรือ push.

## Files Changed

- `.ai/specs/console-auth-config-contract/requirements.md` — created — approved EARS requirements.
- `.ai/specs/console-auth-config-contract/design.md` — created — approved design.
- `.ai/specs/console-auth-config-contract/tasks.md` — created — task status และ Evidence จริง.
- `.ai/specs/console-auth-config-contract/HANDOFF.md` — created — handoff นี้.
- `.env.example` — edited — canonical variables และ non-secret Microsoft examples.
- `docker-compose.prod.yml` — edited — rename key ฝั่งซ้ายเท่านั้น; external inputs และ behavior เดิมคงอยู่.
- `docs/reference/admins.md` — edited — canonical session/CORS names.
- `docs/runbooks/local-dev-run.md` — edited — canonical setup, migration map, export/restart steps.
- `src/BuildingBlocks/BuildingBlocks.Web/CorsExtensions.cs` — edited — typed validated CORS options.
- `src/Hosts/Api/ConsoleConfiguration.cs` — created — provider-aware resolver, snapshot, validation, warning service.
- `src/Hosts/Api/Admins/AuthOptions.cs` — edited — `WebAppBaseUrl`.
- `src/Hosts/Api/Admins/LoginService.cs` — edited — canonical redirect base.
- `src/Hosts/Api/Merchants/UserOidcOptions.cs` — edited — `MerchantSession` และ `WebAppBaseUrl`.
- `src/Hosts/Api/Merchants/UserLoginService.cs` — edited — canonical redirect base.
- `src/Hosts/Api/Merchants/UserRegistration.cs` — edited — registration/invitation canonical base.
- `src/Hosts/Api/Program.cs` — edited — single snapshot registration และ typed CORS wiring.
- `src/Hosts/Api/appsettings.json` — edited — canonical base contract.
- `src/Hosts/Api/appsettings.Development.json.example` — edited — canonical local example.
- `src/Hosts/Api/Properties/launchSettings.json` — edited — canonical Merchant web-app key.
- `tests/Architecture.Tests/HostTestConfigGateTests.cs` — edited — pin lazy post-build resolver capture.
- `tests/Architecture.Tests/LocalDevelopmentOriginTests.cs` — edited — pin canonical tracked artifacts และ Compose RHS.
- `tests/Hosts.Tests/ConsoleConfigurationResolverTests.cs` — created — aliases, normalization, conflict, precedence.
- `tests/Hosts.Tests/ConsoleConfigurationStartupTests.cs` — created — startup validation matrix และ warnings.
- `tests/Hosts.Tests/TestConfigurationExtensions.cs` — created — remove machine-local Development JSON from hermetic hosts.
- `tests/Hosts.Tests/AdminAuthLoginRedirectTests.cs` — edited — hermetic host config.
- `tests/Hosts.Tests/AdminCorsGuardTests.cs` — edited — canonical CORS config และ hermetic host.
- `tests/Hosts.Tests/AdminLoginServiceTests.cs` — edited — canonical option property.
- `tests/Hosts.Tests/CorsTests.cs` — edited — canonical origins และ dual-console union coverage.
- `tests/Hosts.Tests/InvitationStartProviderTests.cs` — edited — canonical invitation link test.
- `tests/Hosts.Tests/MerchantUserAuthLoginRedirectTests.cs` — edited — canonical session config.
- `tests/Hosts.Tests/MerchantUserLoginServiceTests.cs` — edited — canonical option property.
- `tests/Hosts.Tests/MicrosoftAuthLoginRedirectTests.cs` — edited — canonical session config.
- `tests/Hosts.Tests/OidcCallbackE2ETests.cs` — edited — canonical keys และ hermetic host config.

## Important Decisions

- Resolver capture เกิดก่อน `builder.Build()` แต่ resolve จริงใน hosted startup หลัง provider layering ครบ.
- Committed `appsettings.json` และ C# initializers เป็น baseline; provider อื่นเป็น explicit operator input.
- Canonical/legacy explicit values ที่ normalize แล้วต่างกันหยุด startup; errors และ warnings แสดง key เท่านั้น.
- Session และ CORS consumers ใช้ snapshot เดียวตลอด process; config change ต้อง restart.
- CORS ยังคง Admin, Merchant และ dual-console path classification กับ `AllowCredentials` เดิม.
- Test hosts ที่ต้องกำหนด canonical config ตัด machine-local `appsettings.Development.json` เพื่อให้ผล deterministic.
- Compose เปลี่ยนเฉพาะชื่อ key เดิมตาม approved design; ไม่เพิ่ม allowlist behavior ใหม่.
- ไม่อ่านหรือแก้ `.env` จริง และไม่แก้ ignored `src/Hosts/Api/appsettings.Development.json`.

## Constraints

- ห้าม commit secret, `.env`, credential file หรือ ignored Development config.
- ห้ามเปลี่ยน callback URI, auth scheme/cookie, REST/OpenAPI, database หรือ UI ใน spec นี้.
- ห้าม push ตรง `develop`; ต้องผ่าน PR. ห้าม force push.

## Tests Run

- `dotnet restore pol-core.slnx` -> passed.
- `dotnet build pol-core.slnx --no-restore -warnaserror` -> passed, 0 warnings, 0 errors.
- `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationResolverTests` -> 6 passed.
- `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~ConsoleConfigurationStartupTests` -> 31 passed.
- focused auth/CORS/invitation command ใน `tasks.md` -> 52 passed.
- `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --filter FullyQualifiedName~LocalDevelopmentOriginTests` -> 1 passed.
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,796 passed.
- solution-wide `Category=Integration` filter, human-run output -> 150 passed, 0 failed, 0 skipped, 17.6s.
- 16 warnings จาก SQL integration invocation มาจาก 16 non-integration assemblies ที่ไม่มี test ตรง filter;
  `Integration.Tests` ผ่านโดยไม่มี warning.
- `.ai/bin/check-secrets.sh --all` -> passed.
- `scripts/spec-trace.sh console-auth-config-contract` -> 91/91 criteria referenced; EARS lint passed.
- `git diff --check` -> passed.
- rename contract check -> passed; legacy literals remain only in compatibility code/tests และ migration map.
- viewport: n/a — ไม่มี UI change.

## Known Issues

- ไม่มี blocking issue ที่ทราบ.
- User-run test invocation สรุป `Build succeeded with 16 warning(s)` เพราะ solution-wide filter ไม่พบ
  integration tests ใน 16 assemblies; required build gate แยกต่างหากผ่าน 0 warnings, 0 errors.
- Codex ไม่ได้อ่าน `.env` หรือ secret value; SQL result มาจาก output ที่ human operator ส่งให้.

## Next Recommended Agent

Reviewer สำหรับตรวจ diff ก่อนเปิด PR.

## Next Steps

1. ตรวจ diff และ Evidence ใน `tasks.md`.
2. เปิด PR เข้า `develop` ด้วย `$ship-pr develop` เมื่อพร้อม.
