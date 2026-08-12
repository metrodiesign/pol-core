# Handoff Note: Frontend Real API Integration

## Task Summary

รองรับ real API integration สำหรับ `pol-merchant` และเตรียม contract inventory สำหรับ `pol-admin`.

## Current Status

Merchant backend/frontend implementationและ offline gatesผ่าน. Final spec/standards re-review PASS ทั้งสอง repo.
Admin implementationยังไม่เริ่ม เพราะต้อง reconcile design ที่ซ้ำกับ Merchant/Core ก่อน.

## Files Changed

- `src/Hosts/Api/**` — OpenAPI, Merchant auth/session/registration, commerce, order, payment และ user/RBAC endpoints
- `src/Modules/{Merchants,Orders,Payments,Products,Iam}/**` — application/domain contracts, permissions และ lifecycle
- `src/Persistence/Persistence.{MerchantRuntime,MerchantUsers}/**` — paged queries, invitation/audit/outbox persistence
- `src/BuildingBlocks/**` — coded request/conflict errors และ migration snapshot
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260809183210_MerchantRealApiIdentity*` — schema migration
- `tests/**` — host, module, architecture, concurrency, OpenAPI และ integration coverage
- `.env.example`, `docker-compose.prod.yml` — PSP, OIDC return path และ forwarded-header configuration
- `.ai/specs/frontend-real-api-integration/handoff.md` — current cross-repo state

## Important Decisions

- Merchant permission keysเป็น `payment.view`, `payment.create`, `payment.redirect`, `users.view`,
  `users.manage`, `users.roles`, `roles.view`, `roles.manage`.
- Invitationผูก MerchantId + verified email; consumeแบบ atomic; manager lookupไม่รั่ว tenant.
- Checkoutรับ `paymentMethod`, ไม่รับ client amount; payment channel persistพร้อม order.
- PSP defaultอ่าน `Psp:DefaultCode`; unsupported/unavailableคืน coded 409 ไม่มี silent fallback.
- Money OpenAPI/JSON amountเป็น non-negative fixed four decimals.
- Product `productCode`/`variantCode` และ paged `totalPages` required/non-nullใน OpenAPI.

## Constraints

- Branchปัจจุบัน `codex/merchant-real-api-integration`; ห้าม pushตรง `main`/`develop`.
- ห้าม commitก่อน review.
- Admin specอยู่ `/Users/king_developer/Desktop/Project/pol-admin/.claude/specs/real-api-integration/`;
  explicit human approvalยังขาด.

## Tests Run

- `dotnet test pol-core.slnx --filter "Category!=Integration" --no-restore` -> 1,604 passed
- SQL integrationรอบปัจจุบันไม่ได้รัน เพราะเครื่องไม่มี `POL_APP_PASSWORD`/`POL_SA_PASSWORD`;
  evidence ก่อน reconciliation -> 145 passed
- `dotnet build pol-core.slnx --no-restore` -> 0 warnings, 0 errors
- `.ai/bin/check-secrets.sh --all` -> passed
- `scripts/spec-trace.sh frontend-real-api-integration` -> 57/57
- Mermaid parser -> 2/2 diagrams passed
- live `health/live`, `health/ready`, OpenAPI -> passedบน `http://localhost:5100`
- Merchant frontend full gate -> 313 tests, lint 0 errors, typecheck/build/audit/spec trace passed
- Browser 375/768/1440 + Lighthouse -> public/unauth flows passed

## Known Issues

- SQL integrationต้องรันซ้ำเมื่อ inject local test principals; ห้ามใช้ค่าปลอมหรือ print secret.
- Admin designเดิมมี ownership/operation ซ้ำกับ Merchant/Core และต้องแก้ก่อน implementation.
- Merchant protected browser walkthroughต้องใช้ Google account/sessionจริง; ไม่มี bypass/mock.
- Worktreesมี uncommitted user/intended changes; review scopeก่อน stage.

## Next Recommended Agent

human review แล้วใช้ spec implementation flowสำหรับ Adminหลัง approval

## Next Steps

1. Userตรวจ Merchant diffและ protected flowด้วย accountจริงถ้ามี.
2. Userตอบ `อนุมัติ design` ใน Admin taskเพื่อสร้าง/อนุมัติ tasks.
3. หลัง review แยก commit/PRต่อ repoเข้า `develop`.
