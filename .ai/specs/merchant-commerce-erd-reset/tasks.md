# Implementation Tasks: Merchant-Commerce ERD Reset

> Status: approved 2026-08-07

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. ERD canon และ schema vocabulary foundation — copy ERD เข้า `docs/reference`, บันทึก deviation/retain
     allowlist, เปลี่ยน Admin/IAM/CFG/Merchant/User/Vault/DataProtection domain + API + dual persistence mappings
     ตามชื่อ/status target และทำ effective permission resolution แบบ Active-only โดยคง vault encryption กับ
     retained fieldsครบ. Done = model parity, rename/status, IAM resolver และ no-stale-identifier tests ผ่าน.
     Satisfies: REQ-1, REQ-2, REQ-11.12, REQ-13.1, REQ-13.5. Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`.
     Evidence:
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> 952 tests passed before Architecture project; combined run canceled when pre-existing compile-negative test deadlocked on sequential redirected-stream reads
       - test: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --filter "FullyQualifiedName!~CompileNegativeReferenceTests"` -> 221 passed / 0 failed; `dotnet build tests/Architecture.Tests/Fixtures/ForbiddenControlPlaneReference/Bad.csproj --nologo` -> expected exit 1 with `Forbidden ProjectReference`
       - viewports: n/a — backend/schema logic only
       - deviations: requirement/design none; fixed compile-negative test harness to drain stdout/stderr concurrently, preventing pipe deadlock exposed by larger build output

- [x] 2. KYC photo lifecycle end-to-end — เพิ่ม optional multipart `kycPhoto`, validation 2 MiB/type/magic,
     deterministic staged object operation, key-only User persistence, transactional lifecycle outbox, idempotent
     commit/delete consumer และ 24-hour orphan TTL โดย omission คง keyเดิมและไม่มี public read/review/status surface.
     Done = success, resubmission, DB rollback, process-crash/replay, TTL และ no-key-log/response/history tests ผ่าน.
     Satisfies: REQ-3, REQ-11.7, REQ-13.4, REQ-13.29. Depends on: 1. Verify: `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Kyc"`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 0 warnings / 0 errors; `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-build` -> 150 passed; focused ticket/login/response host tests -> 16 passed; `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter "FullyQualifiedName~Kyc"` -> 1 passed
       - test: full `Hosts.Tests` attempted; unrelated live catalogue test requires missing `POL_SA_PASSWORD`, so focused non-DB KYC host coverage used instead
       - review: security/code review found and fixed committed-object deletion on ticket replay, conflicting deterministic content, concurrent local staging, unbounded keyed locks, and object-key leakage through provider exceptions; final pass no actionable findings
       - viewports: n/a — multipart/backend lifecycle only
       - deviations: requirement/design none; added narrow `DiscardStagedAsync` compensation seam so ambiguous/replayed requests cannot delete an already committed KYC object

- [x] 3. Typed native-JSON contracts และ EF compatibility configuration — map approved five columns เท่านั้น,
     เพิ่ม typed/canonical commerce กับ Merchant metadata codecs, reject unknown/secret-shaped metadata, จำกัด
     provisioning/public-outbox payload และ configure provider compatibility 170 ทุก runtime/design/provisioning path.
     Done = model metadata parity, allowlist negative tests, event payload privacy และ architecture call-site scan ผ่าน;
     actual engine/database/invalid-write proofอยู่ task 8.
     Satisfies: REQ-4.1-4.9, REQ-11.9. Depends on: 1. Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 0 warnings / 0 errors; `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~NativeJson|FullyQualifiedName~ProvisioningCoordinatorTests|FullyQualifiedName~MerchantIdentityLifecycleTests"` -> 28 passed; `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-build` -> 154 passed; merchant provisioning host binding tests -> 10 passed; SharedKernel tests -> 81 passed
       - test: full non-integration gate passed every completed test project; expected `ModelConsistencyTests` failure remains because final model snapshot is deliberately regenerated once in task 8, then long-running compile-boundary checks were canceled after result was isolated
       - review: security/code review removed PII and secret hints from native-JSON/outbox payloads, closed event registries, enforced typed unknown-field rejection, and validated provisioning replay shape; final pass no actionable task-3 findings
       - viewports: n/a — persistence contracts/codecs only
       - deviations: actual SQL Server engine, database compatibility, migration snapshot and invalid native-JSON write proof remain task 8 as approved

- [x] 4. Generic insurance Cart vertical slice — เปลี่ยน CartItem เป็น product/variant contract, resolve source ด้วย
     server credentials, snapshot price/name/PII-free metadata, รองรับ quantity มากกว่า 1 และ sold guardหนึ่งครั้งต่อ
     line พร้อมรักษา merchant query/write isolation และ concurrency Version. Done = Cart API/domain/source/error matrix
     และ quantity/metadata/isolation tests ผ่าน.
     Satisfies: REQ-5, REQ-7.11, REQ-11.1, REQ-11.2, REQ-13.2, REQ-13.3. Depends on: 1, 3. Verify: `dotnet test tests/Carts.Tests/Carts.Tests.csproj && dotnet test tests/Products.Tests/Products.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj`.
     Evidence:
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors; Carts -> 31 passed; Products -> 112 passed; Cart/native-JSON/read/write architecture slice -> 19 passed; merchant lifecycle/dependency/error HTTP slice -> 70 passed
       - test: HTTP host runs used `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false`; diagnosis proved sandbox config file watcher hung inside `WebApplication.CreateBuilder`, while same host completed immediately with reload disabled; no production workaround retained
       - review: security/code review confirmed closed request contract, server-only price/name/metadata, PII-free typed metadata, one sold probe for quantity >1, tenant guard, dual mapping parity and no partial Version bump on rejected source snapshots; no actionable findings remain
       - viewports: n/a — backend Cart/API contract only
       - deviations: full Hosts project still contains a separate live-catalogue test requiring `POL_SA_PASSWORD`; task-owned HTTP suites ran instead

- [x] 5. Direct Cart-to-Order และ privacy-aware read surfaces — เพิ่ม authorized/CSRF-protected
     `POST /api/v1/orders`, final as-of source probe, owner-port/shared-UoW atomic commit, optimistic Cart conflict,
     generic OrderItem/zero discount, customer summary no metadata และ audited merchant detail reveal. Done = HTTP
     status/contractครบ, rollbackไม่มี partial row, concurrent Cart commitได้หนึ่งครั้ง และ tenant/privacy tests ผ่าน.
     Satisfies: REQ-6, REQ-7.1-7.10, REQ-7.12-7.20, REQ-11.3-11.6, REQ-11.8, REQ-13.6-13.10, REQ-13.25, REQ-13.30. Depends on: 3, 4. Verify: `dotnet test tests/Orders.Tests/Orders.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj`.
     Evidence:
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors; `dotnet test tests/Orders.Tests/Orders.Tests.csproj --no-build` -> 85 passed; Hosts excluding live-SQL catalogue + task-8 snapshot -> 493 passed; focused order transaction -> 3 passed; focused architecture transaction/native-JSON/read/write slice -> 27 passed
       - test: direct HTTP proof covers 201 + Location/Money/Pending, server metadata, quantity, claimed mismatch, missing SaleCode, CSRF, sold guard และ retry 409; SQLite shared-UoW proof covers full rollback, stale Version และ two-request one-winner
       - review: code/security review confirmed actor-owned SaleCode, merchant-scoped reload/write guard, source-before-transaction validation, server-only price/metadata, PII-free summary/list, audited fail-closed detail และ one-save atomic commit; no actionable finding remains
       - viewports: n/a — backend Order/API/persistence logic only
       - deviations: live SQL Integration suite could not run because `POL_APP_PASSWORD`/`POL_SA_PASSWORD` are absent; attempted Order-filter run failed only at environment guards. Final SQL schema/snapshot remains task 8 by approved design; in-memory SQL transaction and HTTP gates passed.

- [x] 6. Serialized Order payment lifecycle — เพิ่ม versioned `PaymentPaid/Failed/Expired` contracts/registry,
     emitterเดียว, atomic attempt attachment/retry, Order row-lock primitive สำหรับทุก lifecycle writer, stale-event
     correlation, terminal cancel guard และ late/second-paid conflict alert/reconciliation evidence. Done = unit,
     concurrency และ E2E payment success/failure/expiry/retry/cancel tests ผ่านโดย webhook/idempotency controlsเดิมคงอยู่.
     Satisfies: REQ-9, REQ-11.10, REQ-11.11, REQ-13.15-13.17, REQ-13.26, REQ-13.27. Depends on: 5. Verify: `dotnet test tests/Payments.Tests/Payments.Tests.csproj && dotnet test tests/Orders.Tests/Orders.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj`.
     Evidence:
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors; Payments -> 261 passed; Orders -> 96 passed; payment/cancel/atomicity/policy Host slice -> 45 passed; Hosts excluding live-SQL catalogue + task-8 snapshot -> 498 passed; Architecture excluding task-8 snapshot -> 234 passed
       - test: lifecycle coverage proves atomic attempt attachment rollback, Failed/Expired retry, stale correlation no-op, late Paid acceptance, second-Paid/Cancelled reconciliation poison, terminal cancel, one row-lock winner และ stable versioned outbox registry; existing webhook idempotency tests stay green
       - test: focused SQL Server integration 7 tests attempted; all stopped only at environment guards because `POL_APP_PASSWORD`/`POL_SA_PASSWORD` are absent, before database access
       - review: code/security review fixed tenant-unscoped raw row locks, canonical-method retry lookup, attached-Paid mint race, mismatched-money replay, retry summary reissue, policy status exhaustiveness และ pre-PSP failure classification; narrow raw-lock ports now require explicit `OrderId + MerchantId`; no actionable findings remain
       - viewports: n/a — backend lifecycle/outbox/concurrency only
       - deviations: final migration snapshot/open-session SQL index and real SQL Server row-lock proof remain task 8 by approved design; in-memory atomicity, concurrency, architecture and HTTP gates passed

- [x] 7. Checkout และ policy surface retirement — ลบ Checkouts projects/tables/contracts/DI/routes และ
     OrderItem policy entities/readers/reports/write routes/escape hatches พร้อมลบ IAM groups/keys/grantsให้เหลือ
     19 keys/7 groups; old routesต้อง 404และ production treeต้องไม่มี stale references. Done = solution references,
     OpenAPI removals, route tests, seed parity และ architecture banผ่าน.
     Satisfies: REQ-8, REQ-12.3, REQ-12.4, REQ-12.12, REQ-13.11-13.13. Depends on: 5, 6. Verify: `dotnet build pol-core.slnx -warnaserror && dotnet test tests/Architecture.Tests/Architecture.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Iam.Tests/Iam.Tests.csproj`.
     Evidence:
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors; IAM -> 60 passed; Architecture excluding task-8 snapshot -> 225 passed; retirement/OpenAPI/IAM/write-floor Host slice -> 71 passed
       - test: seven retired Checkout/policy routes return 404; OpenAPI contains no retired path; canonical IAM catalog is exactly 19 keys / 7 groups / 25 grants across four seed roles
       - scan: production tree excluding legacy migration history has no `Checkouts`, `CheckoutSession`, `CheckoutConfirmed`, `ItemPolicy`, policy-report route, policy permission or solution reference
       - review: removed Checkout projects/contracts/routes/DI, policy entities/readers/writers/reports/grants, stale worker insert authority and dead Cart reopen flow; direct Cart-to-Order and payment update paths remain green
       - viewports: n/a — backend/API/schema surface retirement only
       - deviations: physical legacy migration/table history removal is intentionally task 8 fresh-baseline work; task 7 production model and public surface are clean

- [x] 8. Guarded SQL Server 2025 fresh baseline — ลบ legacy migration/snapshot filesและสร้าง
     `InitialSchema -> SecurityObjects -> SeedData`, fail-before-DDL บน non-empty/legacy target, pin RTM-CU5+
     engine/image + DB/provider level 170, สร้าง raw objects/grants/seeds และ rollback non-prod แบบ dependency-safe.
     Release wrapperต้องบังคับ exact target, explicit reset approval, backup URI/checksum และ rollback evidence;
     ห้าม auto-reset production. Done = fresh apply/down, refusal, full schema และ real round-trip valid/invalid JSON
     ครบห้า columnsผ่านบน SQL Server target.
     Satisfies: REQ-4.10-4.13, REQ-10, REQ-13.14, REQ-13.23, REQ-13.24, REQ-13.28. Depends on: 1-7. Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration" && bash docker/bootstrap/assert-fresh-db.test.sh`.
     Evidence:
       - migration: legacy 30-migration history/snapshot replaced by exactly `InitialSchema -> SecurityObjects -> SeedData`; current snapshot has no pending model changes and no retired Checkout/policy table
       - live SQL: full Integration suite on isolated fresh SQL Server 2025 scratch database -> 144 passed; focused fresh apply/down, non-empty refusal, legacy-history refusal and five-column native-JSON valid/invalid round-trip -> 5 passed
       - live SQL: `assert-fresh-db.sql` passed after fresh apply and `database update 0` completed dependency-safe; scratch database was then removed, production/catalog `VCentralPay` untouched
       - test: `bash docker/bootstrap/assert-fresh-db.test.sh` -> OK; `bash docker/migrate-entrypoint.test.sh` -> 51 passed / 0 failed; build -> 0 warnings / 0 errors
       - operations: bootstrap/migration enforce engine >= 17.0.4045.5 and database/provider compatibility 170; CI/dev images pin `2025-CU5-ubuntu-24.04` plus immutable digest; production migrator requires exact target, `RESET_APPROVED=true`, backup URI/SHA-256, approval evidence and rollback evidence before DB access
       - review: raw `RegistrationNotices`, `OrderNoSeq`, least-privilege grants, 19/7/25 IAM seed, active cfg seed and synthetic disabled demo merchant/PSP are asserted on real catalog; invalid JSON rejected by all five native columns
       - viewports: n/a — database/release infrastructure only
       - deviations: none; rollback uses `Down` only for non-production proof, while production procedure requires backup restore evidence

- [x] 9. Consumer cutover docs และ release assembly gate — publish final OpenAPI Cart/Order contracts, เขียน
     `FE-MIGRATION.md` พร้อม route/request/response/status/error mappings, sync architecture/reference/runbooks และ
     wire CI/staging/secret/spec-trace/backup/rollback gates โดยไม่แก้ frontend repositoryอื่น. Done = full solution
     warnings-as-errors + all tests + secret scan + spec traceผ่าน และ staging reset/smoke/rollback rehearsalมี evidence.
     Satisfies: REQ-12.1, REQ-12.2, REQ-12.5-12.11, REQ-13.18-13.24. Depends on: 1-8. Verify: `dotnet build pol-core.slnx -warnaserror && dotnet test pol-core.slnx && scripts/spec-trace.sh merchant-commerce-erd-reset && .ai/bin/check-secrets.sh --all`.
     Evidence:
       - contract: runtime OpenAPI booted from real Development host; focused published `openapi-cart-order.yaml` covers Cart CRUD and direct `POST /orders`; Host contract test locks `productCode`/`variantCode`, server-owned price/metadata, 201 response, 400/403/404/409/503 and retired Checkout/policy absence
       - consumer: `FE-MIGRATION.md` maps old/new routes, request/response, Money, Cart/Order status, ProblemDetails UX and big-bang no-alias cutover; frontend repositories were not modified
       - docs: architecture/coding standards, IAM/Merchants/Orders/entity/DB/source references, README/env and local/prod runbooks synced to 4-module flow, 19/7/25 IAM, three-migration fresh baseline and backup-restore production rollback
       - release: `release-gate.yml` checks out exact version tag, requires tag-specific changelog, staging evidence, protected production target match, backup URI/SHA-256, reset approval and rollback evidence; production assembly depends on staging + protected environment approval and does not deploy by itself
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors; full offline solution suite -> 1,562 passed / 0 failed; isolated fresh SQL Server 2025 Integration suite -> 144 passed / 0 failed; fresh catalog assertion passed and scratch DB was removed
       - gate: release evidence tests, migration wrapper 51/0, fresh assertion static test, migration lineage, YAML/shell syntax and `git diff --check` passed; `scripts/spec-trace.sh merchant-commerce-erd-reset` -> 264/264 requirements covered; `.ai/bin/check-secrets.sh --all` -> passed
       - review: code/security review fixed branch-instead-of-tag release checkout, non-versioned changelog acceptance and unbound production target evidence; final review found no actionable findings
       - staging: `STAGING-EVIDENCE.md` records isolated reset/apply/assert/smoke-equivalent Integration/Down rehearsal; production gate still requires fresh environment-specific staging URI and human approval
       - deviations: first Integration attempt used existing legacy `VCentralPay` from `.env` and failed on old columns; no reset was attempted there. Rerun on validated isolated scratch baseline passed 144/144, then exact scratch target was removed.

## Suggested execution batches

> COUPLED feature: tasks share schema, domain contracts, DbContexts, migrations และ API composition.
> Default รันทั้งหมด sessionเดียวตาม dependency order:
> `scripts/pane-loop.sh merchant-commerce-erd-reset all-in-one` หรือ `/spec-implement all`.
> ไม่กำหนด `Batch:` เพราะแต่ละ taskใหญ่และมี verification boundaryต่างกัน. Task 8 ต้องทำหลัง 1-7 เพื่อ scaffold
> final modelครั้งเดียว; task 9เป็น assembly/release gateสุดท้าย.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
