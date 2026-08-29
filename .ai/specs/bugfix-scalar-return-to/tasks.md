# Implementation Tasks: Scalar OIDC return redirect

แก้ root cause ที่ Development config และ callback redirect พร้อม regression test ที่ตรวจผลลัพธ์ URL จริง

> Status: approved 2026-08-08

- [x] 1. Route allowlisted Scalar return target to API origin — เพิ่ม Development-only `/scalar` allowlist และ configured Scalar origin, ให้ callback เลือก Scalar originเฉพาะ path `/scalar`, รักษา frontend redirect และ open-redirect fallback เดิม, เพิ่ม regression tests สำหรับ Scalar และ dashboard/fallback.
     Satisfies: F-1, F-2, B-1, B-2, B-3
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter FullyQualifiedName~AdminLoginServiceTests`.
     Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter FullyQualifiedName~AdminLoginServiceTests`.
    - deviations: full `Hosts.Tests` did not finish after test host startup and was cancelled; focused callback suite and solution build passed. Local `appsettings.Development.json` is ignored by policy; tracked `appsettings.Development.json.example` and `.env.example` carry the safe configuration template.
