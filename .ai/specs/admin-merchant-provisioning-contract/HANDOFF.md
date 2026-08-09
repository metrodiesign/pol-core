# Handoff Note: Admin Merchant Provisioning Contract

> From: Codex root session   To: human review   Date: 2026-08-09

## Task Summary

Quick spec `admin-merchant-provisioning-contract` ยืนยัน REQ-1–REQ-4 ของ provisioning/vault/approval flow
ที่มีอยู่ เพิ่ม reference/ERD และ HTTP boundary regression tests โดยไม่เปลี่ยน production API หรือ schema

## Current Status

Task 1 complete. Spec artifacts approved และ implementation tests/gates ผ่าน พร้อมส่ง review ผ่าน PR.

## Files Changed

- `.ai/specs/admin-merchant-provisioning-contract/` — requirements, design, completed task evidence และ handoff ใหม่
- `docs/reference/merchants.md` — provisioning contract, error map, register/approve sequence และ vault custody
- `docs/reference/merchant-commerce-payment-erd-revised-kyc-simplified.md` — logical Vault relationships
- `tests/Hosts.Tests/MerchantApprovalEndpointTests.cs` — HTTP 404/409/200 approval-boundary regression tests ใหม่
- `tests/Architecture.Tests/ProvisioningCoordinatorTests.cs` — assert provisioning ไม่สร้าง reveal audit

## Important Decisions

- Reuse `POST /api/v1/merchants`; ไม่เพิ่ม endpoint/entity เพราะ existing flow ครบแล้ว
- Merchant provisioning เป็น atomic complete setup; ไม่มี Draft หรือ deferred PSP configuration
- Registration ยังสร้าง unbound PendingApproval; Merchant prerequisite บังคับตอน approval
- Vault relationships เป็น logical scope ผ่าน `MerchantId`; ไม่มี physical FK หรือ cascade delete

## Constraints

- Merchant code จำกัด `vprivilege`, `vcommerce`, `vsouvenir`
- ห้ามเพิ่ม plaintext reveal endpoint, Vault admin UI/API หรือ Merchant CRUD โดยไม่มี requirement ใหม่
- ห้าม commit/push ตรง `main` หรือ `develop`; งานนี้ยังไม่ commit

## Tests Run

- `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors
- `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-build --no-restore` -> 157 passed / 0 failed
- `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --no-restore` -> 444 passed / 0 failed
- `dotnet test pol-core.slnx --no-build --no-restore --filter "Category!=Integration"` -> ทุก offline project ที่จบผ่าน; `Architecture.Tests` full suite 233/233
- `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --no-restore --filter "Category!=Integration"` -> 444 passed / 0 failed
- Focused HTTP approval tests -> 3 passed / 0 failed
- Focused provisioning coordinator tests -> 10 passed / 0 failed
- `source .env.integration` + `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-restore` -> 145 passed / 0 failed
- `source .env.integration` + `scripts/check-migration-lineage.sh` -> current 4-migration chain passed
- CI guard regression scripts, `.ai/bin/check-secrets.sh --all`, `scripts/check-rename-identifiers.sh`, spec-trace ทุก spec และ `git diff --check` -> passed
- `scripts/spec-trace.sh admin-merchant-provisioning-contract` -> 22/22 covered, EARS lint passed

## Known Issues

- Solution-level offline command รอ `Hosts.Tests` file watcher บน macOS หลัง project อื่นจบ; rerun Hosts พร้อม `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false` ผ่าน 444/444.
- Mermaid parser reported pre-existing `purify.addHook is not a function` on canonical document flowchart block; both newly changed ERD blocks passed.

## Next Recommended Agent

Human review. No implementation work remains.

## Next Steps

1. Read spec artifacts and inspect current worktree diff.
2. If approved, commit on feature branch and open PR through repository workflow.
