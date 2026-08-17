# Implementation Tasks: Local Development Origins

สอง task ครอบ API และ SPA origin contracts พร้อม verification

> Status: approved 2026-08-17 (quick, no gates; amended SPA origins)

- [x] 1. Make `https://localhost:5001` the canonical local API origin across launch configuration, active local examples, scripts, current docs and Scalar redirect tests while preserving production and historical values
  Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5
  Verify: static active-surface port audit, targeted Hosts tests, solution build and `scripts/spec-trace.sh local-api-port-5001`
  Evidence:
  - `dotnet build pol-core.slnx --no-restore -warnaserror -m:1 /nodeReuse:false /p:UseSharedCompilation=false` passed with 0 warnings and 0 errors
  - Active runtime/config/source/test/script surfaces contain no local `localhost:5100` or `localhost:5101`; production self-host references remain unchanged
  - Committed and ignored local JSON parse successfully and pin API/Scalar/PSP public origin to `https://localhost:5001`
  - API started from the committed launch profile and listened on loopback port `5001`

- [x] 2. Align customer, admin and merchant local SPA origins across committed/local config, OIDC redirects, PSP browser returns, current docs and regression tests while preserving historical evidence
  Satisfies: REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7, REQ-2.8
  Verify: committed-config regression test, targeted login redirect tests, active-surface port audit, solution build and `scripts/spec-trace.sh local-api-port-5001`
  Evidence:
  - Direct execution passed `LocalDevelopmentOriginTests` plus admin and merchant `With_SpaBaseUrl...` redirect tests (3/3)
  - Active surfaces contain no `localhost:5200` or `localhost:5300` except negative regression assertions
  - `scripts/spec-trace.sh local-api-port-5001` passed all 13 criteria; `.ai/bin/check-secrets.sh --all` and `git diff --check` passed
  - External terminal full non-integration suite passed 1756/1756 with 0 failed and 0 skipped; targeted Hosts regression passed 2/2

## Verification Snapshot

- Runtime, ignored local appsettings, committed appsettings example, tooling, docs and regression fixtures use `https://localhost:5001`
- Solution build passed with 0 warnings and 0 errors
- Customer, admin and merchant origins are `https://localhost:3000`, `https://localhost:3001` and `https://localhost:3002`
- Changed SPA behavior passed 3 direct checks; external terminal full non-integration suite passed 1756/1756
- Spec trace passed 13/13 criteria
