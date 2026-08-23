# Handoff Note: admin-workforce-jit

งาน Microsoft Workforce JIT ปิด Tasks 1–5 สำหรับ implementation และ local acceptance แล้ว. Task 6 เป็น Staging release gate ที่ยังต้องใช้ Entra/SQL/browser จริง.

## Current Status

`implementation complete / production release blocked`

- `pol-core`: Tasks 1–5 checked with local evidence.
- `pol-admin`: auth bypass ถูกลบ; dirty changes อื่นถูกเก็บไว้เดิม, ห้าม reset/checkout/clean.
- `tasks.md`: Task 6 ยัง unchecked เพราะ staging Entra/SQL/live acceptance ไม่ครบ.
- `docs/runbooks/admin-workforce-jit-rollout.md`: staging, Super bootstrap, session revoke และ rollback checklist.

## Important Decisions

- Admin OIDC ใช้ Microsoft เท่านั้น: fixed tenant, `vcp.employee`, และ exact `viriyah.co.th` domain.
- eligible identity ใหม่ได้ `Active + Tier.Scoped` โดยไม่มี role หรือ merchant assignment.
- identity key ใช้ `(provider=microsoft, oid)` และ audit JIT ไม่เก็บ raw external identity หรือ email.
- Google Admin login/callback ไม่ register และคืน `404`; Merchant Google flow คงเดิม.
- ไม่มี Staging จึงแยก local acceptance ออกจาก Staging release gate; ห้ามใช้ Task 5 เป็น production approval.

## Files Changed

- `src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs` — typed workforce claim gate และ failure classifier.
- `src/Hosts/Api/Admins/OidcAuthentication.cs` — Microsoft-only Admin OIDC callback.
- `src/Hosts/Api/Admins/LoginService.cs` — JIT dispatch, typed conflict mapping, post-commit session.
- `src/Hosts/Api/Program.cs` — Production Microsoft provider guard.
- `src/Modules/Admins/Admins.Application/Users/JitProvisionMicrosoftAdmin.cs` — atomic JIT handler.
- `src/Persistence/Persistence.ControlPlane/Admins/*` — fresh conflict-recovery reader/context factory.
- `src/Modules/Admins/Admins.Domain/Users/*` — JIT factory and audit action.
- `tests/Admins.Tests/*`, `tests/Hosts.Tests/*`, `tests/Architecture.Tests/*` — gate and contract coverage.
- `src/Hosts/Api/appsettings.json` — remove obsolete Admin Google allowlist note/config.
- `docs/runbooks/local-dev-run.md` — remove obsolete Admin Google setup and allowlist instructions.
- `docs/reference/admins.md` — update Admin auth/API reference to Microsoft workforce contract.
- `docs/runbooks/admin-workforce-jit-rollout.md` — cross-repo rollout and rollback runbook.

## Tests Run

- `dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-restore` -> 125 passed.
- `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --filter "Category!=Integration"` -> 286 passed.
- `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter "FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~ProvisioningGuardsTests"` -> 88 passed.
- `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --filter "Category!=Integration"` -> 286 passed.
- `scripts/spec-trace.sh admin-workforce-jit` -> 115/115 requirements covered.
- `npm test` in `pol-admin` -> Node 23 passed, root Vitest 274 passed, `@pol/shared` 26 passed.
- `npm run typecheck` in `pol-admin` -> root, `@pol/ui`, `@pol/shared` passed.
- `npm run lint` in `pol-admin` -> root, `@pol/ui`, `@pol/shared` passed.
- `npm run build` in `pol-admin` -> Next 16.3.1 build passed, 115/115 static pages generated.
- `dotnet test pol-core.slnx --filter "Category=Integration"` โดยโหลด `/private/tmp/pol-core.integration.env` และไม่ log ค่า -> Integration.Tests 168/168 ผ่าน, Architecture integration 4/4 ผ่าน.
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` ภายใต้ ambient Admin Microsoft configuration หลัง `bugfix-host-test-tenant-pin` -> 1,936/1,936 ผ่าน; `Hosts.Tests` 652/652 ผ่าน.
- Logout regression: endpoint คง authenticated-session contract ตาม REQ-10.9; regression test ยืนยัน `401` เมื่อ session หายหรือ stale และ CSRF filter เดิมยังบังคับ authenticated mutations. Targeted backend checks ผ่าน 18/18; frontend `npm test` ผ่าน 23 + 274 + 26.
- Browser production build `http://127.0.0.1:3101`: login controls ผ่านที่ exact `clientWidth` 375/768/1440, ไม่มี horizontal overflow; error copy และกลับหน้า login ผ่าน.

## Known Issues

- Browser replay ของ production Admin UI ทำได้แล้วผ่าน Codex in-app browser; เป็น UI-only evidence ไม่ใช่ live Entra/JIT evidence.
- Ambient Admin Microsoft configuration ที่เคยทำให้ DB-less `Hosts.Tests` เปิด real control-plane store ถูกแยกด้วย `Hosts.Tests.runsettings`; production tenant pin ไม่เปลี่ยน.
- Integration credentials ใช้งานได้ผ่าน temporary copy; ค่าถูกโหลดเข้า process โดยไม่แสดงใน output.
- Live Entra staging access, SQL race/rollback, session enumeration/revoke and role-refresh browser flow remain unverified in Task 6.

## Next Steps

1. ตั้ง Entra App Role, Assignment required, direct security-group membership และ Conditional Access ใน staging.
2. รัน browser acceptance ครบ eligible JIT, zero-permission 403, role refresh, collision, suspended, Hotmail/onmicrosoft, Google 404, Merchant Google, logout และ revoke.
3. ทำ staging Super bootstrap/session-revoke rehearsal และ rollback image rehearsal.
4. Mark Task 6 เฉพาะเมื่อทุกผลข้างต้นมี evidence จริง; ห้าม deploy Production ก่อนหน้านั้น.

## Next Recommended Agent

Operator ที่มี staging Entra ดำเนิน Task 6 ต่อ; ห้าม mark จน live evidence ครบ.
