> Status: unknown
# Handoff Note: Local Development Origins

## Task Summary

กำหนด local backend API เป็น `https://localhost:5001` และกำหนด SPA origins เป็น customer `3000`,
admin `3001`, merchant `3002` ผ่าน config, redirects, CORS, PSP browser return, tests และเอกสาร

## Current Status

Implementation complete. Build, static checks, spec trace และ changed-behavior direct checks ผ่านทั้งหมด

ผู้ใช้ทำ Tier 1 account/OTP interaction สำเร็จแล้ว และ backend redirect ไป Merchant SPA `https://localhost:3002/register` ถูกต้อง

## Files Changed

- `src/Hosts/Api/Properties/launchSettings.json` — API HTTPS port `5001`
- `.env.example`, `src/Hosts/Api/appsettings.Development.json.example` และ ignored local appsettings — API, SPA, CORS และ PSP origins
- `src/Hosts/Api/Admins/AuthOptions.cs`, `src/Hosts/Api/Merchants/UserOidcOptions.cs` — current origin examples
- `tests/Architecture.Tests/LocalDevelopmentOriginTests.cs` — committed-config regression guard
- `tests/Hosts.Tests/AdminLoginServiceTests.cs`, `MerchantUserLoginServiceTests.cs`, `OidcCallbackE2ETests.cs` — admin/merchant redirect fixtures
- `README.md`, `docs/runbooks/local-dev-run.md`, `docs/reference/admins.md` — local topology และ commands
- `.ai/specs/local-api-port-5001/*` — requirements, design, tasks และ evidence

## Important Decisions

- OIDC callback URI ยังคงอยู่ API origin `https://localhost:5001`; SPA origins ใช้หลัง callback สำเร็จ
- Customer SPA ใช้ same-origin `/api` proxy จึงไม่ถูกเพิ่มเข้า credentialed admin/merchant CORS
- PSP browser returns ไป customer SPA `https://localhost:3000/checkout/return`
- Backend repo นี้ไม่มี frontend source; frontend repos ต้องตั้ง HTTPS ports `3000`, `3001`, `3002` แยกเอง

## Tests Run

- Solution build with warnings as errors -> passed, 0 warnings, 0 errors
- Direct regression runner -> passed 3/3: committed origin contract, admin redirect, merchant redirect
- JSON parsing and safe origin projection for committed/ignored local appsettings -> passed
- Active-surface audit for old API/SPA ports -> passed; only negative assertions or production/historical exclusions remain
- `scripts/spec-trace.sh local-api-port-5001` -> passed 13/13
- `.ai/bin/check-secrets.sh --all` -> passed
- `git diff --check` -> passed
- API launch -> process listened on `127.0.0.1:5001` and `[::1]:5001`
- External terminal full non-integration suite -> passed 1756/1756, 0 failed, 0 skipped
- External terminal provider-discriminator SQL integration suite -> passed 3/3
- Targeted Hosts regression -> passed 2/2
- Live curl -> sandbox network namespace could not reach the separately launched loopback process; no application response was evaluated

## Security Action Required

ระหว่างตรวจ local ignored appsettings มี credential ถูกแสดงใน agent transcript โดยไม่ตั้งใจ ห้ามใช้ค่าชุดเดิมต่อ

ต้อง rotate/revoke Google OIDC client secrets, local database credentials และ Vault master key ที่เกี่ยวข้องทั้งหมด
การลบไฟล์หรือแก้ git history ไม่พอ เพราะ transcript ยังเก็บค่าเดิม

## Next Steps

1. Rotate credentials ตามหัวข้อ Security Action Required
2. ถ้า secret-bearing `.env` มี override ให้ sync ค่า non-secret origins จาก `.env.example` โดยไม่เปิดเผยค่าอื่น
3. เปิด customer/admin/merchant SPAs บน HTTPS ports `3000`, `3001`, `3002`
4. ทดสอบ Tier 0 แบบ interactive เพิ่มเมื่อมี employee account; Tier 1 callback ผ่านแล้ว
