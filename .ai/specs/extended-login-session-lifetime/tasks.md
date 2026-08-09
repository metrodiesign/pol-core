# Tasks: Extended Login Session Lifetime

> Status: approved 2026-08-09 (quick, no gates)

หนึ่ง task ครอบ vertical slice ทั้ง Admin และ Merchant User เพราะใช้ configuration pattern เดียวกัน

- [x] 1. เปลี่ยน fallback default และ committed config เป็น idle 24 ชั่วโมง / absolute 7 วันทั้งสองฝั่ง,
  เพิ่ม regression assertions สำหรับ expiry และยืนยัน cookie ยังไม่ persistent
  Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5, REQ-1.6, REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4.
  Verify: `scripts/spec-trace.sh extended-login-session-lifetime`, targeted `Hosts.Tests`,
  `dotnet build -warnaserror`, และ `dotnet test`
  Evidence:
  - Red: targeted `Hosts.Tests` -> failed 4, passed 54; login expiry ยังเป็น 30 นาที และ sliding expiry ไม่ขยับเป็น 24 ชั่วโมง
  - Green: targeted `Hosts.Tests` -> passed 58/58; `Admins.Tests` -> passed 95/95; `Merchants.Tests` -> passed 157/157
  - Build: `dotnet build pol-core.slnx --no-restore -warnaserror` (ใน .NET 10 SDK container) -> 0 warnings, 0 errors
  - Offline suite: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> passed 1,594/1,594, failed 0, skipped 0
  - Live integration: `dotnet test pol-core.slnx --filter "Category=Integration"` บน SQL Server 2025 CU5 แยก 3 instance -> passed 144/144, failed 0, skipped 0
  - Trace: `scripts/spec-trace.sh extended-login-session-lifetime` -> 10 criteria covered
  - CI guards: guard regression tests, full-tree secret scan, rename-identifier gate และ spec trace ทุก spec -> passed
  - Fresh DB: `./scripts/check-migration-lineage.sh` และ `docker/bootstrap/assert-fresh-db.sql` -> passed
  - Scope checks: scoped `dotnet format --verify-no-changes`, `git diff --check` และ `jq empty src/Hosts/Api/appsettings.json` -> passed
  - Format deviation: full-solution `dotnet format --verify-no-changes --no-restore` พบ whitespace baseline ในไฟล์นอก scope; scoped changed C# files ผ่าน
