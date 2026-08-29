# Implementation Tasks: Offices authorization after one-based migration

เพิ่ม HTTP regression coverage แล้ว repair local bootstrap admin แบบ targeted โดยคง authorization policy เดิม

> Status: approved 2026-08-09

- [x] 1. Add Offices authorization regression coverage — ขับ real endpoint pipeline ด้วย fake admin authentication, bound permission scope และ fake `IOfficeStore`; assert `200/403/401`, GET CSRF exemption, deny-before-store และ `page=1&limit=25`; pin sibling list-route permission metadata.
     Satisfies: F-1, F-2, F-3, F-4, B-1, B-2, B-3, B-4, B-7
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~OfficeAuthorizationEndpointTests` และ existing CSRF/permission suites.
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter FullyQualifiedName~OfficeAuthorizationEndpointTests` และ existing CSRF/permission suites.

- [x] 2. Repair and verify current local environment — หยุด stale Kestrel, start current one-based build, transactional repair เฉพาะ target admin พร้อม preflight, authorization-version bump, idempotent `platform_admin` assignment และ append-only audit; re-login แล้วตรวจ DB effective permission กับ Offices response; รัน format, build, non-integration, integration และ spec-trace gates.
     Satisfies: F-1, B-5, B-6
     Verify: read-only DB post-check, `dotnet format pol-core.slnx --verify-no-changes --no-restore`, `dotnet build pol-core.slnx --no-restore -warnaserror`, `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`, `dotnet test pol-core.slnx --filter "Category=Integration"`, `scripts/spec-trace.sh bugfix-offices-403`.
     Verify: read-only DB post-check, `dotnet format pol-core.slnx --verify-no-changes --no-restore`, `dotnet build pol-core.slnx --no-restore -warnaserror`, `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`, `dotnet test pol-core.slnx --filter "Category=Integration"`, `scripts/spec-trace.sh bugfix-offices-403`.
  Evidence: หยุด PID `21517`; current backend PID `97984` ฟัง `127.0.0.1:5100`, `/health/live` -> `200`. Transactional repair รอบแรก -> `ProfileChanged=1, RoleAssigned=1`; รอบสอง -> `0,0` พิสูจน์ idempotency. Post-check target -> `Tier=2`, `Status=1`, `AuthorizationVersion=1`, active Platform `platform_admin`, `HasEffectiveUserManage=1`; `OtherAdminRoleAssignments=0`; audit `tier-changed`, `reactivate`, `role-assigned` อย่างละหนึ่ง. หลัง login ใหม่ browser request `GET /api/v1/offices?page=1&limit=25` -> `200` และแสดง 8 รายการ. `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings/errors; targeted test -> 7/7; non-integration suites -> Hosts 451/451, Architecture 233/233 และ module suites ทั้งหมดผ่าน; integration -> 144/144; `scripts/spec-trace.sh bugfix-offices-403` -> exit 0. Scoped format `dotnet format pol-core.slnx --verify-no-changes --no-restore --include tests/Hosts.Tests/OfficeAuthorizationEndpointTests.cs` -> exit 0. Full-solution format command -> exit 2 จาก whitespace baseline หลายไฟล์นอก scope; ไม่มี failure ในไฟล์ใหม่. รายละเอียดและ SQL อยู่ใน `HANDOFF.md`.
