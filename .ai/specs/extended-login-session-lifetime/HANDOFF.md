> Status: unknown
# Handoff Note: Extended Login Session Lifetime

## Task Summary

ขยาย server-side login session ของ Admin และ Merchant User จาก idle 30 นาที / absolute 8 ชั่วโมง เป็น idle 24 ชั่วโมง / absolute 7 วัน ตาม `extended-login-session-lifetime` task 1

## Current Status

Done. โค้ด, regression tests, canonical specs, reference docs และ changelog อัปเดตแล้ว

## Files Changed

- `src/Hosts/Api/Admins/AuthOptions.cs` — Admin fallback defaults
- `src/Hosts/Api/Merchants/UserOidcOptions.cs` — Merchant User fallback defaults
- `src/Hosts/Api/appsettings.json` — committed runtime defaults ทั้งสองฝั่ง
- `tests/Hosts.Tests/*Session*Tests.cs`, `tests/Hosts.Tests/*LoginServiceTests.cs` — expiry, sliding และ non-persistent cookie regression assertions
- `.ai/specs/extended-login-session-lifetime/` — requirements, design, tasks และ handoff ใหม่
- `.ai/specs/admin-oidc-session/`, `.ai/specs/producer-google-sso/`, `.ai/specs/admin-account-management/design.md`, `docs/reference/admins.md` — เอกสารเดิมให้ตรงค่าใหม่
- `CHANGELOG.md` — Unreleased entry

## Important Decisions

- Idle timeout 24 ชั่วโมง; absolute lifetime 168 ชั่วโมง
- Rotation 15 นาทีและ grace period 60 วินาทีคงเดิม
- Cookie ยังไม่มี `Expires`/`Max-Age`; ปิด browser แล้วยังสิ้นสุด session ฝั่ง browser
- Session เดิมไม่ rewrite `AbsoluteExpiresAt`; ต้อง login ใหม่จึงได้ hard cap 7 วันเต็ม

## Constraints

- ห้าม push ตรง `main`/`develop`; ต้อง review และ PR
- ห้ามลด rotation, reuse detection, revocation หรือ per-request account-status checks

## Tests Run

- Targeted auth/session tests ก่อนแก้ -> failed 4, passed 54
- Targeted auth/session tests หลังแก้ -> passed 58/58
- `dotnet test tests/Admins.Tests --no-build --no-restore` -> passed 95/95
- `dotnet test tests/Merchants.Tests --no-build --no-restore` -> passed 157/157
- `dotnet build pol-core.slnx --no-restore -warnaserror` (ใน .NET 10 SDK container) -> 0 warnings, 0 errors
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> passed 1,594/1,594
- `dotnet test pol-core.slnx --filter "Category=Integration"` บน SQL Server 2025 CU5 แยก 3 instance -> passed 144/144
- `scripts/spec-trace.sh extended-login-session-lifetime` -> passed, 10 criteria covered
- CI guard regression, full-tree secret scan, rename-identifier gate, spec trace ทุก spec, migration lineage และ fresh DB contract -> passed
- Scoped `dotnet format --verify-no-changes`, `git diff --check` และ appsettings JSON parse -> passed

## Known Issues

- Full-solution format มี pre-existing whitespace baseline นอก scope

## Next Recommended Agent

Human review

## Next Steps

1. Review diff และรอ CI บน PR ให้เขียว
2. Deploy ผ่าน staging; ให้ผู้ใช้ login ใหม่เพื่อรับ absolute lifetime 7 วัน
