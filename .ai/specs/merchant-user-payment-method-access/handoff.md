# Handoff Note: Merchant User Payment Method Access

> Schema ตาม `.ai/shared/AGENT_HANDOFF_PROTOCOL.md`

## Task Summary

Implement spec `merchant-user-payment-method-access` Tasks 1-8 ครบ: normalized capability catalog/policy, effective resolver, Admin/self APIs, Order/Payment enforcement, anonymous first-charge authorization และ deterministic rollout/cutover/rollback

## Current Status

Done. Requirements, design และ tasks approved; implementationครบทุก task; final gateเขียวบน local SQL Server integration environment

## Files Changed

- `.ai/specs/merchant-user-payment-method-access/` — requirements, design, tasks evidence และ handoff (new)
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/` — additive capability migrationsกับ model snapshot (new/edited)
- `src/Modules/Payments/` — capability contracts, catalog entities, Order/Session/redirect enforcement และ PSP compatibility projection (new/edited)
- `src/Modules/Orders/` และ `src/Hosts/Api/Orders/` — immutable Order method/audience, canonical authorized creation path และ retire legacy `CreateOrderCommand` (edited/deleted)
- `src/Persistence/Persistence.ControlPlane/Payments/`, `src/Persistence/Persistence.MerchantRuntime/Payments/` และ `src/Persistence/Persistence.Provisioning/` — stores, resolver, locks, migration service และ provisioning compatibility (new/edited)
- `src/Hosts/Api/ControlPlane/`, `src/Hosts/Api/Payments/`, `src/BuildingBlocks/BuildingBlocks.Web/CorsExtensions.cs` — Admin/self endpoints, OpenAPI, permission และ CORS routing (new/edited)
- `tests/Architecture.Tests/`, `tests/Hosts.Tests/`, `tests/Integration.Tests/`, `tests/Payments.Tests/`, `tests/Orders.Tests/`, `tests/Merchants.Tests/` — constraint, isolation, race, acceptance และ regression coverage (new/edited)
- `scripts/check_rename_identifiers.py` — ignore tracked files deleted in working tree; gateยังตรวจไฟล์ปัจจุบันครบ (edited)

## Important Decisions

- Missing/disabled policy row deny; adapter `SupportedMethods` เป็น hard ceiling
- Orderเก็บ canonical payment method, initiating audience และ User idจาก server; client overrideไม่ได้
- Authorization writer/read ใช้ global-before-Merchant transaction-owned lock orderเดียวกัน
- Rolloutมี `LegacyRead`, `NormalizedRead`, `FailClosed`; cutoverต้อง verify conflict/drain/deltaครบใน transactionเดียว
- Internal enumใช้ `User = 1` เพื่อไม่คืน retired bare identifier `MerchantUser`; persisted valueไม่เปลี่ยน
- SQL acceptance testsสร้างและลบ scratch databases; ไม่มี production migrationหรือ deployment

## Constraints

- ห้าม pushตรง `main`/`develop`, force push หรือ commitก่อน review
- Production migrationต้องมี backup/rollback และ human confirmation; งานนี้ยังไม่ได้รัน production migration
- ห้ามเพิ่ม UI, `MOBILE_BANKING`, environment versioning, user-level option table หรือ live Omise PromptPay
- รักษา wire codes `card`, `promptpay`, `installment`
- Preserve unrelated untracked `docs/spikes/generic-cart-order-design-plan-prompt.md`

## Tests Run

- `dotnet restore pol-core.slnx` -> passed
- `dotnet build pol-core.slnx --no-restore -warnaserror` -> passed, 0 warnings/0 errors
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> passed, 1,782 tests
- `source .env.integration && dotnet test pol-core.slnx --no-build --filter "Category=Integration"` -> passed, 163 SQL Server tests
- `scripts/check-rename-identifiers.sh` -> passed
- `.ai/bin/check-secrets.sh --all` -> passed
- `scripts/spec-trace.sh merchant-user-payment-method-access` -> passed, 225 criteria referenced and EARS lint clean

## Known Issues

None in approved scope. Production migration and deployment intentionally not run.

## Next Recommended Agent

Human review, then code review/security review before commit and PR

## Next Steps

1. Review diff against approved requirements/design/tasks and migration SQL
2. Commit on feature branch, open PR into `develop`, wait for required CI
3. Plan staging migration with backup, verification and rollback; production only after staging passes
