# Implementation Tasks: Scalar OIDC return redirect

แก้ root cause ที่ Development config และ callback redirect พร้อม regression test ที่ตรวจผลลัพธ์ URL จริง

> Status: approved 2026-08-08

- [x] 1. Route allowlisted Scalar return target to API origin — เพิ่ม Development-only `/scalar` allowlist และ configured Scalar origin, ให้ callback เลือก Scalar originเฉพาะ path `/scalar`, รักษา frontend redirect และ open-redirect fallback เดิม, เพิ่ม regression tests สำหรับ Scalar และ dashboard/fallback. Satisfies: F-1, F-2, B-1, B-2, B-3. Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter FullyQualifiedName~AdminLoginServiceTests`.

  Evidence:
    - test: pre-fix `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter 'FullyQualifiedName~AdminLoginServiceTests.With_ScalarBaseUrl_the_scalar_returnTo_is_absolute_to_the_api_origin'` -> failed as expected (`5200/scalar` actual vs `5100/scalar` expected); post-fix -> Passed 1, Failed 0
    - test: pre-fix dedicated-state regression (`FullyQualifiedName~Login_properties_preserve_returnTo|FullyQualifiedName~Callback_prefers_the_dedicated_returnTo_item`) -> Failed 2, Passed 0 (`.admin.returnTo` missing; callback selected `/dashboard`); post-fix -> Passed 2, Failed 0
    - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore --filter 'FullyQualifiedName~AdminLoginServiceTests'` -> Passed 16, Failed 0
    - test: `dotnet build pol-core.slnx --no-restore --nologo` -> Build succeeded, 0 warnings, 0 errors
    - test: `scripts/spec-trace.sh bugfix-scalar-return-to` -> exit 0; bugfix spec recognized and traceability check skipped by tool
    - viewports: n/a — backend redirect
    - deviations: full `Hosts.Tests` did not finish after test host startup and was cancelled; focused callback suite and solution build passed. Local `appsettings.Development.json` is ignored by policy; tracked `appsettings.Development.json.example` and `.env.example` carry the safe configuration template.
