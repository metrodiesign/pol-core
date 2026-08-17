# Design: Local Development Origins

ย้าย live local API และ SPA surfaces ไป origin ที่กำหนดโดยแก้ค่าเดิมตรงจุด ไม่เพิ่ม abstraction หรือ dependency

> Status: approved 2026-08-17 (quick, no gates; amended SPA origins)

## Decisions

- `launchSettings.json` เหลือ profile `https` เดียวที่ `https://localhost:5001` เพื่อให้ `dotnet run` default ตรง OIDC callback
- Local URL ทุกจุดใช้ทั้ง scheme และ port เดียวกัน ไม่คง HTTP alias บน port อื่น
- Production mapping, deployment runbook, migrations, retrospectives และ Evidence เก่าคงเดิม เพราะเป็นคนละ contract หรือบันทึกอดีต
- Customer SPA เป็น PSP browser-return target ที่ `https://localhost:3000`; ไม่เพิ่ม customer origin เข้า credentialed console CORS
- Admin SPA ใช้ `https://localhost:3001`; merchant SPA ใช้ `https://localhost:3002`
- Ignored `appsettings.Development.json` แก้เฉพาะ non-secret origin โดยไม่อ่านหรือแตะ credential

## Files

| Area | Files | Change |
|---|---|---|
| Runtime | `src/Hosts/Api/Properties/launchSettings.json` | bind HTTPS `5001` เพียง origin เดียว |
| Local config | `.env.example`, `src/Hosts/Api/appsettings.Development.json.example`, ignored `appsettings.Development.json` | align API, SPA, CORS และ PSP browser-return origins |
| Tooling | `scripts/dev-2c2p-webhook.sh` | เปลี่ยน default API base |
| Current docs | `README.md`, `docs/runbooks/local-dev-run.md`, `docs/reference/admins.md`, `.ai/shared/stack/dotnet.md` | เปลี่ยนคำสั่ง, proxy และ port reference |
| Auth contract | `src/Hosts/Api/Admins/AuthOptions.cs`, `src/Hosts/Api/Merchants/UserOidcOptions.cs`, `tests/Hosts.Tests/AdminLoginServiceTests.cs`, `tests/Hosts.Tests/MerchantUserLoginServiceTests.cs`, `tests/Hosts.Tests/OidcCallbackE2ETests.cs`, `.ai/specs/bugfix-scalar-return-to/bugfix.md` | pin Scalar, admin และ merchant redirects |
| Payment return | `src/Modules/Payments/Payments.Infrastructure/Psp/PspOptions.cs` | document customer SPA as browser-return owner |
| Config regression | `tests/Architecture.Tests/LocalDevelopmentOriginTests.cs` | pin committed API/SPA/CORS/PSP example literals |
| Consistency | `tests/Hosts.Tests/ProvisioningGuardsTests.cs`, `.ai/specs/microsoft-oidc-ciam-alignment/requirements.md`, `CHANGELOG.md` | retire active local `5100` example และปิด port question |

## Verification

- Static audit หา `5100`/`5101` ใน active local surfaces ต้องไม่พบ
- Static audit หา `5200`/`5300` ใน active local config, source comments, current docs และ live test fixtures ต้องไม่พบ
- Architecture regression test ต้องยืนยัน committed example ใช้ customer/admin/merchant origins ตาม REQ-2
- Targeted Hosts tests ต้องยืนยัน Scalar redirect และ public base URL guard
- Solution build ต้องผ่านโดยไม่มี warning/error
- Spec trace ต้องครอบทุก criterion

## Requirement Traceability

| Requirement | Design element |
|---|---|
| REQ-1.1 | single HTTPS launch profile |
| REQ-1.2 | local config, tooling and current-doc sweep |
| REQ-1.3 | Scalar option, regression test and amended bugfix contract |
| REQ-1.4 | ignored local appsettings alignment |
| REQ-1.5 | explicit production/historical/error-code exclusions |
| REQ-2.1 | customer PSP return config and local topology docs |
| REQ-2.2 | admin session/CORS config, docs and tests |
| REQ-2.3 | merchant session/CORS config, docs and tests |
| REQ-2.4 | admin login service fixtures and local configuration |
| REQ-2.5 | merchant login service fixtures and local configuration |
| REQ-2.6 | 2C2P/Omise browser-return configuration |
| REQ-2.7 | separate admin and merchant credentialed CORS origins |
| REQ-2.8 | explicit historical-evidence exclusion |
