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

- [x] 2. KYC photo lifecycle end-to-end — เพิ่ม optional multipart `kycPhoto`, validation 2 MiB/type/magic,
     deterministic staged object operation, key-only User persistence, transactional lifecycle outbox, idempotent
     commit/delete consumer และ 24-hour orphan TTL โดย omission คง keyเดิมและไม่มี public read/review/status surface.
     Done = success, resubmission, DB rollback, process-crash/replay, TTL และ no-key-log/response/history tests ผ่าน.
     Satisfies: REQ-3, REQ-11.7, REQ-13.4, REQ-13.29. Depends on: 1. Verify: `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~Kyc"`.

- [x] 3. Typed native-JSON contracts และ EF compatibility configuration — map approved five columns เท่านั้น,
     เพิ่ม typed/canonical commerce กับ Merchant metadata codecs, reject unknown/secret-shaped metadata, จำกัด
     provisioning/public-outbox payload และ configure provider compatibility 170 ทุก runtime/design/provisioning path.
     Done = model metadata parity, allowlist negative tests, event payload privacy และ architecture call-site scan ผ่าน;
     actual engine/database/invalid-write proofอยู่ task 8.
     Satisfies: REQ-4.1-4.9, REQ-11.9. Depends on: 1. Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`.

- [x] 4. Generic insurance Cart vertical slice — เปลี่ยน CartItem เป็น product/variant contract, resolve source ด้วย
     server credentials, snapshot price/name/PII-free metadata, รองรับ quantity มากกว่า 1 และ sold guardหนึ่งครั้งต่อ
     line พร้อมรักษา merchant query/write isolation และ concurrency Version. Done = Cart API/domain/source/error matrix
     และ quantity/metadata/isolation tests ผ่าน.
     Satisfies: REQ-5, REQ-7.11, REQ-11.1, REQ-11.2, REQ-13.2, REQ-13.3. Depends on: 1, 3. Verify: `dotnet test tests/Carts.Tests/Carts.Tests.csproj && dotnet test tests/Products.Tests/Products.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj`.

- [x] 5. Direct Cart-to-Order และ privacy-aware read surfaces — เพิ่ม authorized/CSRF-protected
     `POST /api/v1/orders`, final as-of source probe, owner-port/shared-UoW atomic commit, optimistic Cart conflict,
     generic OrderItem/zero discount, customer summary no metadata และ audited merchant detail reveal. Done = HTTP
     status/contractครบ, rollbackไม่มี partial row, concurrent Cart commitได้หนึ่งครั้ง และ tenant/privacy tests ผ่าน.
     Satisfies: REQ-6, REQ-7.1-7.10, REQ-7.12-7.20, REQ-11.3-11.6, REQ-11.8, REQ-13.6-13.10, REQ-13.25, REQ-13.30. Depends on: 3, 4. Verify: `dotnet test tests/Orders.Tests/Orders.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj`.

- [x] 6. Serialized Order payment lifecycle — เพิ่ม versioned `PaymentPaid/Failed/Expired` contracts/registry,
     emitterเดียว, atomic attempt attachment/retry, Order row-lock primitive สำหรับทุก lifecycle writer, stale-event
     correlation, terminal cancel guard และ late/second-paid conflict alert/reconciliation evidence. Done = unit,
     concurrency และ E2E payment success/failure/expiry/retry/cancel tests ผ่านโดย webhook/idempotency controlsเดิมคงอยู่.
     Satisfies: REQ-9, REQ-11.10, REQ-11.11, REQ-13.15-13.17, REQ-13.26, REQ-13.27. Depends on: 5. Verify: `dotnet test tests/Payments.Tests/Payments.Tests.csproj && dotnet test tests/Orders.Tests/Orders.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Integration.Tests/Integration.Tests.csproj`.

- [x] 7. Checkout และ policy surface retirement — ลบ Checkouts projects/tables/contracts/DI/routes และ
     OrderItem policy entities/readers/reports/write routes/escape hatches พร้อมลบ IAM groups/keys/grantsให้เหลือ
     19 keys/7 groups; old routesต้อง 404และ production treeต้องไม่มี stale references. Done = solution references,
     OpenAPI removals, route tests, seed parity และ architecture banผ่าน.
     Satisfies: REQ-8, REQ-12.3, REQ-12.4, REQ-12.12, REQ-13.11-13.13. Depends on: 5, 6. Verify: `dotnet build pol-core.slnx -warnaserror && dotnet test tests/Architecture.Tests/Architecture.Tests.csproj && dotnet test tests/Hosts.Tests/Hosts.Tests.csproj && dotnet test tests/Iam.Tests/Iam.Tests.csproj`.

- [x] 8. Guarded SQL Server 2025 fresh baseline — ลบ legacy migration/snapshot filesและสร้าง
     `InitialSchema -> SecurityObjects -> SeedData`, fail-before-DDL บน non-empty/legacy target, pin RTM-CU5+
     engine/image + DB/provider level 170, สร้าง raw objects/grants/seeds และ rollback non-prod แบบ dependency-safe.
     Release wrapperต้องบังคับ exact target, explicit reset approval, backup URI/checksum และ rollback evidence;
     ห้าม auto-reset production. Done = fresh apply/down, refusal, full schema และ real round-trip valid/invalid JSON
     ครบห้า columnsผ่านบน SQL Server target.
     Satisfies: REQ-4.10-4.13, REQ-10, REQ-13.14, REQ-13.23, REQ-13.24, REQ-13.28. Depends on: 1-7. Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration" && bash docker/bootstrap/assert-fresh-db.test.sh`.

- [x] 9. Consumer cutover docs และ release assembly gate — publish final OpenAPI Cart/Order contracts, เขียน
     `FE-MIGRATION.md` พร้อม route/request/response/status/error mappings, sync architecture/reference/runbooks และ
     wire CI/staging/secret/spec-trace/backup/rollback gates โดยไม่แก้ frontend repositoryอื่น. Done = full solution
     warnings-as-errors + all tests + secret scan + spec traceผ่าน และ staging reset/smoke/rollback rehearsalมี evidence.
     Satisfies: REQ-12.1, REQ-12.2, REQ-12.5-12.11, REQ-13.18-13.24. Depends on: 1-8. Verify: `dotnet build pol-core.slnx -warnaserror && dotnet test pol-core.slnx && scripts/spec-trace.sh merchant-commerce-erd-reset && .ai/bin/check-secrets.sh --all`.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
