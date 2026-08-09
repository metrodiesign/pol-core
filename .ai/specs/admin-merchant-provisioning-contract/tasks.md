# Implementation Tasks: Admin Merchant Provisioning Contract

> Status: approved 2026-08-09 (quick, no gates)

- [x] 1. Document existing atomic Merchant provisioning/vault relationships and add HTTP approval-boundary regression tests for missing, inactive and active Merchants. Satisfies: REQ-1, REQ-2, REQ-3, REQ-4.
  Evidence:
  - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> build succeeded, 0 warnings / 0 errors
  - test: `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-build --no-restore` -> 157 passed / 0 failed
  - test: `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --no-restore` -> 444 passed / 0 failed
  - test: `dotnet test pol-core.slnx --no-build --no-restore --filter "Category!=Integration"` -> offline projects passed; `Architecture.Tests` 233/233 และ `Hosts.Tests` rerun แยกด้วย `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false` 444/444 เพราะ solution-level process รอ file watcher บน macOS
  - test: focused `MerchantApprovalEndpointTests` -> 3 passed / 0 failed; focused `ProvisioningCoordinatorTests` -> 10 passed / 0 failed
  - test: `source .env.integration` + `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-restore` -> 145 passed / 0 failed หลัง recreate local `VCentralPay` จาก migration chain ปัจจุบัน
  - gate: `source .env.integration` + `scripts/check-migration-lineage.sh` -> `InitialSchema -> SecurityObjects -> SeedData -> OneBasedPersistedEnumStorage`
  - gate: CI guard regression scripts, `.ai/bin/check-secrets.sh --all`, `scripts/check-rename-identifiers.sh`, spec-trace ทุก spec และ `git diff --check` -> passed; `admin-merchant-provisioning-contract` covered 22/22 criteria
  - docs: Mermaid blocks ที่เพิ่มใหม่ผ่าน parser; ERD block ใหม่ใน canonical file และ Vault ERD ใน Merchant reference ผ่าน
  - viewports: n/a — backend contract/documentation only
  - deviations: solution-level offline command ถูกยกเลิกหลังทุก project ยกเว้น `Hosts.Tests` จบ เพราะ local macOS process รอ file watcher; rerun `Hosts.Tests` แยกพร้อมปิด config reload ผ่าน 444/444. `Architecture.Tests` full suite ผ่าน 233/233.
