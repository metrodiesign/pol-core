# Tasks: Hermetic Host Tests for Workforce Tenant Pin

> Status: approved 2026-08-23

One cohesive test-harness fix isolates DB-less host tests from ambient Admin Microsoft configuration while preserving production tenant-pin behavior.

- [x] 1. **Isolate Hosts.Tests configuration** — make testhost ignore ambient Admin Microsoft provider values unless a factory explicitly supplies them, then verify DB-less startup, tenant pin/drift, disabled-provider, real-store, and Merchant authentication contracts.
  Satisfies: F-1, F-2, B-1, B-2, B-3, B-4, B-5.
  Evidence:
  - test RED: repro command from `bugfix.md` before fix -> 0 passed, 1 failed with SQL connection at `Program.cs:626`
  - test GREEN: repro command from `bugfix.md` after fix, without explicit `--settings` -> 1 passed, 0 failed
  - test: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~MicrosoftAuthLoginRedirectTests|FullyQualifiedName~AdminMicrosoftTenantSnapshotTests|FullyQualifiedName~MerchantUserAuthLoginRedirectTests'` under ambient Admin Microsoft variables -> 29 passed, 0 failed
  - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~WorkforceTenantBindingStoreTests'` -> 3 passed, 0 failed
  - test: `set -a && source /private/tmp/pol-core.integration.env && set +a && dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~EntraPreProvisionSqlIntegrationTests'` -> 4 passed, 0 failed
  - test: `dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` under ambient Admin Microsoft variables -> 1,936 passed, 0 failed
  - build: `dotnet build tests/Hosts.Tests/Hosts.Tests.csproj --no-restore` -> succeeded, 0 warnings, 0 errors
  - trace: manual bugfix trace -> 7/7 F/B IDs covered; `scripts/spec-trace.sh` currently skips bugfix specs
  - viewports: n/a — test infrastructure only
  - deviations: none
