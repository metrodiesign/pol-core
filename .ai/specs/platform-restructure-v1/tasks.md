# Implementation Tasks: รื้อ POL Platform รุ่นแรกสำหรับทีมเล็ก

> Status: approved 2026-09-09

ทำตามลำดับ 10 ช่วง แต่ละช่วงส่งมอบพฤติกรรมพร้อม tests ของตัวเอง Task 1–7 ปิดแล้วหลัง focused gates ผ่าน; user ขอหยุดหลัง Task 7 และ Task 8–10 ยัง pending

## ลำดับงาน

- [x] 1. รวม packaging และเก็บ baseline — ย้าย source/test เข้า 4/3 projects โดยคงพฤติกรรมเดิม พร้อม inventory จุดเรียก DI/generated wiring jobs/config และข้อมูลที่ต้องย้าย
  Satisfies: REQ-1.1, REQ-1.2, REQ-1.4, REQ-1.7, REQ-1.8, REQ-11.1, REQ-12.1, REQ-12.5
  Verify: dotnet build pol-core.slnx && dotnet test pol-core.slnx.

  Evidence:

  - test: `NUGET_HTTP_CACHE_PATH=/tmp/pol-task1-nuget-http-cache dotnet build pol-core.slnx` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolPackagingTask1Test; export NUGET_HTTP_CACHE_PATH=/tmp/pol-task1-nuget-http-cache; dotnet test pol-core.slnx --logger 'trx;LogFilePrefix=task1-final' --results-directory /tmp/pol-task1-final-trx` -> exit 0; Unit 1,077, Architecture 355, Integration/Host 919; รวม 2,351 passed, failed 0, skipped 0
  - test: `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> exit 1, 339/340 passed; ข้อเดียวที่ไม่ผ่านคือ `RealRepoCheck.test_real_merge_base_comparator_green` จาก `CI_PROTECTED_JOB_CHANGED`
  - test: `bash docker/entrypoint.test.sh` -> 62/0; `bash docker/migrate-entrypoint.test.sh` -> 59/0; shell suites ตาม CI รวม 16 ชุด exit 0
  - test: `bash .ai/bin/check-secrets.sh --all` -> exit 0 รวมไฟล์ย้ายใหม่ผ่าน intent-to-add; migration parity, rename gate และ spec trace ผ่าน
  - viewports: n/a — backend packaging; HTTP/config behavior ตรวจผ่าน host tests
  - deviations: หลักฐานข้างต้นเป็นรอบก่อน prerequisite merge; current-base gate ด้านล่างใช้ base `fc24dffb` หลัง PR #251 merge
  - current-base: `git diff --check` exit 0 และ `git ls-files -u` ว่าง; SDD scope `untouched`; preservation 18 ผ่าน, skipped 1; full Python 352 ผ่าน, skipped 1; policy alignment `allow` และ regression 63 ผ่าน
  - current-base runtime: build warnings/errors 0; Unit 1,077, Architecture 355, Integration 919 รวม 2,351 passed, failed 0, skipped 0 หลังเริ่ม local SQL compose และ retry หนึ่งครั้ง
  - current-base trace: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` และ `bash scripts/spec-trace.sh platform-restructure-v1` ผ่าน 126 criteria
  - limitations: runtime รอบแรก SQL connection refused เพราะ compose down; solution wrapper detached ก่อนบรรทัด final `EXIT=` แต่ project summaries และ TRX ทั้งสามไฟล์ยืนยันผล green; ยังไม่ใช่ final aggregate audit/review/ship หรือ cutover-ready

- [x] 2. Account, Access และ login ครบเส้นทาง — Employee JIT, pending registration session, human PKCE/BFF, SYSTEM private_key_jwt, account/client kill switch และ scoped authorization ใช้ OAuth state เจ้าของเดียว
  Satisfies: REQ-2, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4, REQ-3.5, REQ-3.6, REQ-3.7, REQ-3.8, REQ-3.10, REQ-3.11, REQ-3.12
  Depends on: 1
  Verify: dotnet test pol-core.slnx --filter "Capability=IdentityAccess".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolIdentityAccessTask2Test; dotnet test pol-core.slnx --filter "Capability=IdentityAccess"` -> exit 0; Unit 8, Architecture 2, Integration 19; รวม 29 passed, failed 0, skipped 0
  - test: `git diff --check` -> exit 0
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria อ้างครบและ EARS lint ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria อ้างครบ
  - evidence: raw output อยู่ `.pipeline/platform-restructure-v1/task2-access-3b.log` และมี `EXIT=0`; coverage matrix อยู่ `.pipeline/platform-restructure-v1/tests-task2.md`
  - environment: ใช้ SQL Server test database แยก `PolIdentityAccessTask2Test`; local compose healthy; source `.env.integration` ภายใน shell เท่านั้น
  - viewports: n/a — backend identity/access; OAuth/BFF/session ตรวจผ่าน host และ SQL Server integration tests
  - deviations: ไม่มี Entra credential หรือ external provider/JWK registration จึงใช้ local host, OpenIddict 7.7.0, registered public JWK และ framework crypto; ไม่ประกาศ live login หรือ production/cutover readiness

- [x] 3. Merchant และตั้งค่า PSP แบบ maker-checker — master data, routing/method eligibility, credential versions, connection test และ emergency stop พร้อม immutable context สำหรับรายการเดิม
  Satisfies: REQ-5
  Depends on: 2
  Verify: dotnet test pol-core.slnx --filter "Capability=MerchantConfiguration".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore` -> exit 0, warnings 0, errors 0
  - test: `dotnet test pol-core.slnx --no-restore --filter "Capability=MerchantConfiguration"` -> exit 0; Unit 144, Architecture 12, Integration 40; รวม 196 passed, failed 0, skipped 0
  - test: `dotnet test tests/Pol.IntegrationTests/Pol.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~MerchantConfigurationGovernanceSqlIntegrationTests"` -> exit 0; 1 passed, failed 0, skipped 0; SQL checker race บน `PolMerchantConfigTask3Test`
  - test: `dotnet ef database update --context PolDbContext --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj` ด้วย `POL_DESIGN_SQL` ชี้ `PolMerchantConfigTask3Test` -> exit 0; migration `20260910035334_Task3MerchantMaster` applied
  - test: `git diff --check` -> exit 0
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria อ้างครบและ EARS lint ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria อ้างครบ
  - evidence: raw logs อยู่ `.pipeline/platform-restructure-v1/task3-master.log`, `task3-migrate.log`, `task3-maker-checker.log`, `task3-maker-sql.log` และ `task3-eligibility-pinning.log` โดยคำสั่งที่เกี่ยวข้องมี `EXIT=0`
  - coverage: matrix อยู่ `.pipeline/platform-restructure-v1/tests-task3.md`; implementation summary อยู่ `.pipeline/platform-restructure-v1/changes-task3.md`
  - environment: local SQL Server ใช้ฐานแยก `PolMerchantConfigTask3Test`; ไม่มี PSP sandbox credential/contract evidence จึงใช้ capture adapters และคง live capability ที่ไม่มีหลักฐานเป็น disabled
  - viewports: n/a — backend merchant/PSP configuration; maker-checker และ eligibility ตรวจผ่าน host และ SQL Server integration tests
  - deviations: `SimpleRoutingControlPlaneTests.OpenApi_pins_the_simple_routing_get_and_put_contracts` ยัง `409` และถูกบันทึกเป็น `UNRESOLVED` ของ Task 8 ใน `state.md`; ไม่ใช่ Task 3 behavior gate และต้องปิดก่อน final ship

- [x] 4. สมัครตัวแทนถึงผลตัดสิน — draft/submit/ประวัติ, reviewer contact evidence, approve/reject แข่งกันได้ผลเดียว และสร้าง Account/Access/outbox ใน commit เดียว
  Satisfies: REQ-4
  Depends on: 2, 3
  Verify: dotnet test pol-core.slnx --filter "Capability=Registration".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolRegistrationTask4ChainTest; dotnet test pol-core.slnx --no-build --filter "Capability=Registration" --blame-hang-timeout 2m` -> exit 0; Integration 4 SQL tests + 1 host test; รวม 5 passed, failed 0, skipped 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolRegistrationTask4ChainTest; dotnet test pol-core.slnx --no-build --filter "Capability=MerchantConfiguration" --blame-hang-timeout 2m` -> exit 0; Unit 144, Architecture 12, Integration 40; รวม 196 passed, failed 0, skipped 0
  - test: `git diff --check` -> exit 0
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria อ้างครบและ EARS lint ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria อ้างครบ
  - evidence: SQL raw log `.pipeline/platform-restructure-v1/task4-sql.log`; host raw log `.pipeline/platform-restructure-v1/task4-host.log`; migration chain `.pipeline/platform-restructure-v1/task4-migration-chain.log`; Task3 regression `.pipeline/platform-restructure-v1/task4-task3-full-regression.log`; coverage อยู่ `.pipeline/platform-restructure-v1/tests-task4.md`
  - database: migration chain ตรวจบน `PolRegistrationTask4ChainTest`; SQL lifecycle fixture ใช้ `PolRegistrationTask4Test` และ host fixture ใช้ random scratch DB เพื่อแยกข้อมูล; table/FK ownership query ของ chain ผ่าน
  - environment: ใช้ local SQL Server, applicant/reviewer host route ต่อ store จริง, external delivery ไม่ได้รัน; outbox event registry มี local publish handler และ Notification materialization อยู่ Task7
  - limitations: ไม่มี live Entra/SMS/Email delivery evidence; ไม่ประกาศ production/cutover readiness
  - viewports: n/a — backend registration workflow; draft/submit/approve/reject ตรวจผ่าน host และ SQL Server integration tests
  - deviations: none

- [x] 5. Order และ PaymentLink — trusted pricing, nullable ownership, draft/issue/cancel, frozen items, link rotation และ customer-safe summary โดยไม่มี Payment aggregate
  Satisfies: REQ-6, REQ-7.1, REQ-7.2, REQ-7.3, REQ-7.4, REQ-7.5
  Depends on: 2, 3
  Verify: dotnet test pol-core.slnx --filter "Capability=OrdersLinks".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=master; dotnet test pol-core.slnx --no-restore --filter "Capability=OrdersLinks" --blame-hang-timeout 2m --logger "console;verbosity=minimal"` -> exit 0; Unit 19, Architecture 3, Integration/Host 7; รวม 29 passed, failed 0, skipped 0
  - test: `git diff --check` -> exit 0
  - test: `bash scripts/check-migration-script.sh` -> exit 0; `docker/migrations/schema.sql` ตรงกับ EF migration head
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria ผ่าน
  - evidence: raw output อยู่ `.pipeline/platform-restructure-v1/task5-links-host.log` มี `EXIT=0`; coverage matrix อยู่ `.pipeline/platform-restructure-v1/tests-task5.md`; implementation summary อยู่ `.pipeline/platform-restructure-v1/changes-task5.md`
  - database: fresh `PolOrdersLinksTask5Test` ใช้ migration chain ถึง `20260910060757_Task5OrdersLinks`; ตรวจ legacy backfill, direct composite FK, active-link uniqueness, protected replay, token hash และ no Payment/Transaction table
  - environment: ใช้ local SQL Server และ Data Protection provider; ไม่มี live Entra/PSP/SMS credential จึงไม่ประกาศ live หรือ cutover readiness
  - viewports: n/a — backend order/payment-link logic; pricing/issue/rotation ตรวจผ่าน host และ SQL Server integration tests
  - evidence: review-fix เพิ่ม HTTP+SQL canonical create false/default true, PATCH→issue→rotate, protected replay, trusted quote rejection และ no-write security checks; Cart compatibility ย้ายไป `/api/v1/orders/from-cart`

- [x] 6. Checkout และ Transaction — per-tab confirm, persist-before-PSP, 2C2P/Omise adapters, callback/return/inquiry, same-reference recovery และ late/duplicate-success handling
  Satisfies: REQ-7.6, REQ-7.7, REQ-7.8, REQ-7.9, REQ-7.10, REQ-8
  Depends on: 3, 5
  Verify: dotnet test pol-core.slnx --filter "Capability=CheckoutTransactions".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolCheckoutTransactionsTask6Test; dotnet test pol-core.slnx --no-build --filter "Capability=CheckoutTransactions" --blame-hang-timeout 2m` -> exit 0; Unit 10, Architecture 2, Integration/Host 13; รวม 25 passed, failed 0, skipped 0
  - test: `set -a; source .env.integration; set +a; export POL_DB=PolCheckoutTransactionsTask6Test; dotnet test pol-core.slnx --no-build --filter "Capability=OrdersLinks" --blame-hang-timeout 2m` -> exit 0; Unit 19, Architecture 3, Integration/Host 7; รวม 29 passed, failed 0, skipped 0
  - test: `bash scripts/check-migration-script.sh` -> exit 0; `docker/migrations/schema.sql` ตรงกับ migration head
  - test: `dotnet ef migrations has-pending-model-changes --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj --context PolDbContext --no-build` -> exit 0; ไม่มี model changes ค้าง
  - test: `git diff --check` -> exit 0
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria ผ่าน
  - evidence: raw output อยู่ `.pipeline/platform-restructure-v1/task6-results.log` และ `.pipeline/platform-restructure-v1/task6-task5-regression.log`; matrix อยู่ `.pipeline/platform-restructure-v1/tests-task6.md`; implementation summary อยู่ `.pipeline/platform-restructure-v1/changes-task6.md`
  - database: fresh `PolCheckoutTransactionsTask6Test` ใช้ migration chain Task2->Task6; ตรวจ `txn.Transactions`, `txn.TransactionEvents`, composite FKs, filtered potential uniqueness, two `SUCCEEDED` rows, append-only events และ persist-before-PSP ผ่าน connection แยก
  - environment: ใช้ local SQL Server และ capture/contract adapters; ไม่มี live PSP credentials หรือ provider sandbox จึงไม่ประกาศ live/cutover readiness
  - viewports: n/a — backend checkout/transaction logic; per-tab confirm, callback/return/inquiry ตรวจผ่าน host และ SQL Server integration tests
  - deviations: live PSP callback/return contract ถูกแทนด้วย local signature/contract capture evidence; Notification materialization และ API inventory ยังคงเป็น Task 7 และ Task 8 ตามลำดับ

- [x] 7. Notification และงานค้าง — materialize Email/SMS จาก control outbox, SMTP reuse, SMS contract, signed business webhook, delivery snapshots, dedupe/retry และ review notes
  Satisfies: REQ-9
  Depends on: 4, 6
  Verify: dotnet test pol-core.slnx --filter "Capability=Notifications".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore -v:minimal` -> exit 0, warnings 0, errors 0
  - test: `set -a; source .env.integration; set +a; unset POL_DB; dotnet test pol-core.slnx --no-build --filter "Capability=Notifications" --blame-hang-timeout 2m --logger "console;verbosity=minimal"` -> exit 0; Architecture 25, Unit 8, Integration/Host 8; รวม 41 passed, failed 0, skipped 0
  - test: `set -a; source .env.integration; set +a; unset POL_DB; dotnet test pol-core.slnx --no-build --filter "Capability=Registration" --blame-hang-timeout 2m --logger "console;verbosity=minimal"` -> exit 0; 5 passed, failed 0, skipped 0
  - test: `set -a; source .env.integration; set +a; unset POL_DB; dotnet test pol-core.slnx --no-build --filter "Capability=CheckoutTransactions" --blame-hang-timeout 2m --logger "console;verbosity=minimal"` -> exit 0; Unit 10, Architecture 2, Integration/Host 13; รวม 25 passed, failed 0, skipped 0
  - test: `dotnet ef migrations list --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj --context PolDbContext --no-build` -> exit 0; migration chain มี Task2–Task7 ครบ รวม `20260910094927_Task7NotificationRuntime` และ `20260910101508_Task7WebhookEndpointUniquenessLive`
  - test: `dotnet ef migrations has-pending-model-changes --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj --context PolDbContext --no-build` -> exit 0; `No changes have been made to the model since the last migration.`
  - test: `bash scripts/check-migration-script.sh` -> exit 0; `docker/migrations/schema.sql` ตรงกับ EF migrations
  - test: `git diff --check` -> exit 0
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; 126 criteria ผ่าน
  - test: `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria ผ่าน
  - evidence: raw focused gate อยู่ `.pipeline/platform-restructure-v1/task7-final.log`; regressions อยู่ `.pipeline/platform-restructure-v1/task7-registration-regression.log` และ `.pipeline/platform-restructure-v1/task7-checkout-regression.log`; static checks อยู่ `.pipeline/platform-restructure-v1/task7-static.log`; matrix อยู่ `.pipeline/platform-restructure-v1/tests-task7.md`; implementation อยู่ `.pipeline/platform-restructure-v1/changes-task7.md`
  - database: fresh `PolNotificationsTask7Test` ตรวจ outbox handoff, inbox/notification/delivery dedupe, Email/SMS fan-out, immutable snapshots, endpoint snapshot, attempt history, retry schedule และ manual queue บน SQL Server จริง
  - environment: ไม่มี live SMS vendor/credential หรือ external provider contract; ใช้ capture adapters และ `BLOCKED_NOT_CONFIGURED`, จึงไม่ประกาศ live delivery/cutover readiness
  - viewports: n/a — backend notification/outbox logic; Email/SMS materialize และ webhook ตรวจผ่าน host และ SQL Server integration tests
  - deviations: aggregate audit/review/ship ยังไม่รัน; user ขอหยุดหลัง Task7 จึงยังไม่เริ่ม API inventory Task8, migration cutover Task9 หรือ legacy retirement Task10

- [x] 8. ตรวจ API และคู่มือใช้งานทั้งระบบ — 111 operations, deferred routes ปิด, child/history/export isolation, errors/idempotency/ETag/audit/health และคู่มือดูแลชุดเดียว
  Satisfies: REQ-3.9, REQ-10, REQ-12.4
  Depends on: 2, 3, 4, 5, 6, 7
  Verify: dotnet test pol-core.slnx --filter "Capability=ApiOperations".

  Evidence:

  C1 bounded slice:

  - test: `dotnet build pol-core.slnx --no-restore -m:1` และ `Task8CommerceC1SqlTests` -> exit 0; real SQL/HTTP flow ครอบ API-081,082,085,091,095–099 บน `PolCommerceC1Task8_202609102120`
  - test: `Capability=OrdersLinks` -> Unit 19 + Architecture 3 + Integration 7 = 29 passed; `Capability=CheckoutTransactions` -> Unit 10 + Architecture 2 + Integration 13 = 25 passed
  - test: `ApiOperationsContractTests` -> expected 111, actual 267, overlap 100, missing 11, deferred 0; เหลือ API-103–107 และ API-111–116 ให้ slice ถัดไป
  - test: `dotnet ef migrations list`/`has-pending-model-changes` และ `scripts/check-migration-script.sh` -> Task8 grants migrations applied, pending model 0, schema drift 0
  - evidence: `.pipeline/platform-restructure-v1/changes-task8.md`, `tests-task8.md`, `task8-operation-matrix.md`, `docs/runbooks/platform-api-v1.md`

  final-wide:

  - test: `Capability=ApiOperations` -> Integration 51 passed, failed 0, skipped 0; data-driven contract test proves all 111 v1 rows, deferred5 absent, metadata/schema/concurrency checksครบ; raw `.pipeline/platform-restructure-v1/task8-final.log`
  - test: regressions `Capability=IdentityAccess` 29, `Capability=OrdersLinks` 29, `Capability=CheckoutTransactions` 25, `Capability=Notifications` 41 -> passed; raw `.pipeline/platform-restructure-v1/task8-final-identity-access.log`, `task8-final-orderslinks.log`, `task8-final-checkouttransactions.log`, `task8-final-notifications.log`
  - test: named OpenAPI 5, SimpleRouting 1, migration list/pending/drift, `git diff --check`, strict contract และ spec trace -> passed; raw `.pipeline/platform-restructure-v1/task8-final-static.log`
  - comparator: EndpointDataSource actual278, overlap111, missing0, deferred0; legacy extras167 จัดหมวดใน `task8-legacy-extras.md` สำหรับ Task10
  - guide: `docs/runbooks/platform-api-v1.md` เป็นคู่มือ canonical ภาษาไทย ครอบ auth contexts, headers, SFS, errors, callbacks, health และ external capability deviations
  - viewports: n/a — backend API inventory/contract; route metadata/authorization/SFS ตรวจผ่าน host และ SQL Server integration tests
  - evidence: API-079 now pins `CreateOrderRequest`; canonical create/patch/issue/rotate runtime path is covered by real HTTP+SQL tests, while `/orders/from-cart` remains an explicit legacy compatibility route outside the canonical inventory

- [x] 9. เครื่องมือย้ายและซ้อม cutover — deterministic ID mapping, conflict report, backfill ไม่มี external side effect, callback recovery และ forward-safe rollback จาก sanitized backup
  Satisfies: REQ-11.2, REQ-11.3, REQ-11.4, REQ-11.5, REQ-11.6, REQ-11.7, REQ-11.8, REQ-11.9, REQ-11.11, REQ-12.2, REQ-12.3
  Depends on: 8
  Verify: dotnet test pol-core.slnx --filter "Capability=MigrationReadiness".

  Evidence:

  - test: `dotnet build pol-core.slnx --no-restore -m:1 -v:minimal` -> exit 0; warnings 0, errors 0
  - test: `dotnet test tests/Pol.UnitTests/Pol.UnitTests.csproj --no-build --filter "Capability=MigrationReadiness" --logger "console;verbosity=minimal"` -> exit 0; 7 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task9-unit.log`
  - test: `set -a; source .env.integration; set +a; export POL_DB=master; dotnet test tests/Pol.IntegrationTests/Pol.IntegrationTests.csproj --no-build --filter "Capability=MigrationReadiness" --logger "console;verbosity=minimal"` -> exit 0; 4 passed, 0 failed, 0 skipped; includes `BACKUP DATABASE`/`RESTORE DATABASE`, SQL target backfill and two-connection lease; raw `.pipeline/platform-restructure-v1/task9-backup.log`
  - test: `set -a; source .env.integration; set +a; export POL_DB=master; dotnet test pol-core.slnx --no-build --filter "Capability=MigrationReadiness" --logger "console;verbosity=minimal"` -> exit 0; Unit 7 + Integration 4 passed, Architecture no matching tests, failed 0, skipped 0
  - test: `bash scripts/check-migration-script.sh` -> exit 0; `docker/migrations/schema.sql` ตรงกับ EF migrations รวม `20260910140000_Task9MigrationReadiness`
  - test: `dotnet ef migrations list --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj --context PolDbContext --no-build` -> exit 0; Task 9 migration อยู่ใน list ต่อจาก Task 8
  - test: `dotnet ef migrations has-pending-model-changes --project src/Pol.Infrastructure/Pol.Infrastructure.csproj --startup-project src/Pol.Api/Pol.Api.csproj --context PolDbContext --no-build` -> exit 0; `No changes have been made to the model since the last migration.`
  - test: `git diff --check` -> exit 0; `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` -> exit 0; `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; 126 criteria ครบ
  - test: mutation ของ duplicate-session conflict reason -> exit 1 ตาม assertion `Expected: PaymentSessionCollision`, `Actual: InvalidPaymentReference`; restore source แล้ว Unit 7/7 ผ่าน
  - viewports: n/a — backend migration/rehearsal logic
  - deviations: ไม่มี sanitized backup/master mapping หรือ external Entra/PSP/Email/SMS authorization; synthetic backup/local isolated SQL พิสูจน์ machinery และ actual target-owner writes เท่านั้น, ยังไม่ใช่ production/cutover readiness

- [x] 10. ถอด legacy และปิดเกณฑ์ส่งมอบ — retire เฉพาะรายการที่มีหลักฐาน, เหลือ 2 runtime contexts/mapping เจ้าของเดียว, ยืนยัน dependency/ownership และ baseline ไม่ถดถอย
  Satisfies: REQ-1.3, REQ-1.5, REQ-1.6, REQ-11.10, REQ-12.6
  Depends on: 9
  Verify: dotnet build pol-core.slnx && dotnet test pol-core.slnx && python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict && bash scripts/spec-trace.sh platform-restructure-v1.

  Evidence:

  - test: `dotnet build pol-core.slnx && dotnet test pol-core.slnx` -> exit 0; Unit 1,143, Architecture 375, Integration 996 passed, failed 0, skipped 0; raw `.pipeline/platform-restructure-v1/task10-final-solution-build2.log` และ `task10-solution-full-final.log`
  - test: `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` และ `bash scripts/spec-trace.sh platform-restructure-v1` -> exit 0; strict contract/trace 126/126, pending-model 0, schema drift 0, route comparator expected111/overlap111/missing0/deferred0; raw Task10 static logs
  - notes: current mapping scan 94/94 duplicate 0, exact owner matrix, lease raw SQL ordering และ Order item replacement/rollback ผ่าน; raw `.pipeline/platform-restructure-v1/task10-raw-lease-targeted.log`, `task10-raw-lease-ordering-sql2.log`, `task10-owner-matrix-targeted3.log`
  - notes: legacy inventory ยังคง KEEP/DEFER rows ที่ไม่มี zero-consumer/expiry/external reference proof; ไม่มี production cutover หรือ destructive retirement
  - viewports: n/a — backend architecture/legacy retirement logic; owner/context guards ตรวจผ่าน architecture และ SQL Server integration tests
  - deviations: retire เฉพาะรายการที่มีหลักฐาน; legacy business routes, customer link aliases, provider callback aliases, recovery paths และ jobs ยัง KEEP/DEFER จนกว่ามี external reference expiry และ zero-consumer proof; ไม่มี production cutover หรือ destructive retirement

## PR #253 review-fix evidence

รายการนี้เป็นหลักฐานเพิ่มเติมหลัง implementation เดิม และ supersede ตัวเลข focused gate เก่าที่อยู่ใน Task 5/7/8:

- Finding 1: pre-fix real SQL dispatcher reproduction ล้มด้วย `PREFIX_DISPATCHER_STATE status=2 attempts=1 leaseOwnerPresent=True deliveredAttempts=0` ใน `.pipeline/platform-restructure-v1/finding1-prefixed-dispatcher-repro.log`; final `Capability=Notifications` ผ่าน Unit 8, Architecture 25, Integration 12 รวม 45 ใน `.pipeline/platform-restructure-v1/finding1-notifications-final.log`.
- Finding 2 workflow: `Task8CommerceC1SqlTests` ผ่าน 3/3 ใน `.pipeline/platform-restructure-v1/finding2-c1-lifecycle-final.log`; ครอบ Draft replay snapshot หลัง PATCH/issue/rotate, default issue/link protected replay, forged quote/adjustment `409 pricing_mismatch`, owner rejection และ no-write SQL assertions.
- Finding 2 auth: real SQL Employee/Agent/System/BFF testsผ่าน 4/4 ใน `.pipeline/platform-restructure-v1/finding2-owner-auth-green2.log`; BFF CSRF test 1/1 อยู่ใน `.pipeline/platform-restructure-v1/finding2-auth-bff-csrf-fixed.log`. ครอบ claim/query merchant mismatch, missing merchant code, human permission, SYSTEM `order.write`, trusted Agent owner, Employee BranchAccess และ BFF missing/invalid/valid CSRF.
- Contract/route: `Capability=ApiOperations` Integration 57/57 ผ่านใน `.pipeline/platform-restructure-v1/finding2-apioperations-final.log`; `PermissionGateSitesTests`, `CsrfParityTests`, write-authorizer และ canonical architecture assertions ผ่านใน `.pipeline/platform-restructure-v1/finding2-auth-write-architecture-final2.log`.
- Regression: `Capability=OrdersLinks` Unit 20, Architecture 3, Integration 9 รวม 32/32 ผ่านใน `.pipeline/platform-restructure-v1/finding2-orderslinks-owner-green3.log`; schema helper 1/1 ผ่านใน `.pipeline/platform-restructure-v1/payment-schema-helper-regression.log`.
- Coverage self-check: atomic claim/lease owner predicate, active no-steal, stale completion, all notification channels, Draft/issued idempotency replay, trusted pricing, adjustment, owner scope, auth/CSRF, rollback/no-write, OpenAPI schema และ legacy route inventory ถูกขับด้วย test จริง. Mutation RED หลักฐานคือ pre-fix dispatcher log, stale-owner completion RED ก่อน ChangeTracker clear, forged quote/adjustment no-write tests และ summary-token filtered-index RED/restore จาก prior review evidence.

## Review-fix PR253 addendum

- BLOCKING #3: identity selector and API-080–088 same-token lifecycle passed with production authorization query, exact scopes, owner checks, CSRF and mixed-context rejection.
- BLOCKING #4: `ICommerceAuthorizationLease` locks and revalidates `acct.Accounts` on the Commerce transaction before replay/idempotency in all six order/link mutations; SQL stale-revoke and lease-first/revoke-waits evidence passed.
- HIGH owner/adjustment/metadata: restricted owner omission, trusted adjustment zero semantics, generic bounded `VersionedMetadata`, duplicate reorder rejection and forward migrations passed on owned SQL scratch databases.
- HIGH notification intent: protected `PaymentLinkNotificationRequestedV1` outbox event, draft/issue/rotate enqueue, replay dedupe, two-channel materialization, delivery-time unprotect, SMS block and rollback evidence passed. Live provider credentials remain unavailable; no production readiness is claimed.
- Deviations: ไม่มี live Entra/PSP/Email/SMS/provider credential หรือ production authorization; local SQL Server, Data Protection, capture adapters และ `BLOCKED_NOT_CONFIGURED` ใช้แทนเฉพาะ protocol/runtime evidence. Provisional webhook stale-owner และ adjustment-absent semantics เป็น review observations ที่ยังไม่เปลี่ยน behavior รอบนี้.

## สิ่งที่ต้องพิสูจน์ในแต่ละช่วง

| Task | เกณฑ์จบที่ต้องมีหลักฐานจริง |
|---|---|
| 1 | นับได้ 4 source/3 tests, baseline เดิมถูกย้ายครบ, ไม่ลบ tests เพื่อให้เขียว, compiler/architecture guard ยังจับ dependency ผิดได้; runtime contexts เดิมอยู่ชั่วคราวจน task 10 |
| 2 | SQL Server JIT race, cross-Merchant guards, no auto-access, replay jti, revoked/expired key, stale authz version, BFF refresh/logout, account-self/platform/merchant policy, CSRF ทุก cookie mutation, revoke-then-commit lease race; pin OpenIddict/EF ตาม design และ restore/build จริง |
| 3 | cross-Merchant FK/write guard, maker ไม่ใช่ checker, stale BaseVersion, secret masking, active config เปลี่ยนแต่ inquiry ของเก่ายังใช้ pinned context |
| 4 | draft ไม่มี Attempt, submit replay ไม่ซ้อน, approve/reject race, Sale เปลี่ยน/ถูกผูกซ้ำแล้ว approve ไม่ผ่าน, applicant มองไม่เห็น internal note; ตรวจ outbox payload ของทั้งสองช่องทาง |
| 5 | ราคาไม่เชื่อ browser, money boundary/rounding, issue atomic, stale ETag, cancelled/paid matrix, rotate/replay link และไม่เปิด raw snapshot |
| 6 | สอง tabs/สอง Idempotency-Keys ไม่สร้าง charge ซ้อน, crash ก่อน/หลังส่ง PSP, timeout ไม่มี failover, callback replay/out-of-order, mismatch, success หลัง cancel และ success จริงสองแถวไม่สูญหาย, cancel/confirm serialize ผ่าน Order เดียว และ terminal result ไม่ถูก downgrade |
| 7 | control-outbox → commerce-inbox แบบ atomic/dedupe, Email/SMS failure ไม่ย้อน approval, recipient/template คงเดิม, accepted ไม่เท่ากับ delivered, timeout เป็น unknown, SSRF/redirect ถูกปฏิเสธ |
| 8 | โหลด inventory เทียบ HTTP route metadata จริง, exact write DTO request/response schema tests และ SFS เดิม, authorization ของทุก operation, child/export ไม่หลุด Merchant, GET status ไม่มี PSP call, audit ไม่หลุด secret/PII |
| 9 | count/mapping/reference/ยอดแยก currency เท่ากัน, ไม่มี fabricated Sale/identity, external-call capture เป็นศูนย์ตอน backfill, replay events หลัง watermark, conflict ทำให้ซ้อมล้มเหลว, ไม่แตะ production |
| 10 | legacy consumers/config/jobs และ callbacks หมดจริงก่อนลบ, architecture tests จับ owner/context ซ้ำ, full suite ผ่านหรือ pre-existing failure มี root-cause evidence พร้อมผลกระทบชัด |

Task 2 ใช้ Merchant/Sale fixtures ที่มาจาก contract จริงใน SQL Server เพื่อพิสูจน์ scope ก่อน Merchant management APIs ของ task 3 พร้อม Task 5 ใช้ transaction-state fixtures สำหรับ cancellation guard และ task 6 ต้องทดสอบ race กับ workflow จริงซ้ำ

Task 7 ส่งมอบ orchestration และสถานะ `BLOCKED_NOT_CONFIGURED` ได้ก่อนทราบ SMS vendor แต่ห้ามบันทึกว่า SMS live-ready ต้องเพิ่ม adapter/contract evidence ของ vendor ที่เลือกก่อนเปิดส่งจริง

## วิธีรันและเก็บหลักฐาน

คำสั่งใน Verify เป็นคำสั่งสำหรับ implementation หลัง task 1 สร้าง target projects แล้ว ไม่ใช่ผลทดสอบของรอบเขียน spec นี้ ใช้ SQL Server test database แยก, clock ที่ควบคุมได้ และ adapters ที่บันทึกจำนวน external calls

กำหนด xUnit trait `Capability` ตามชื่อ filter ในแต่ละ task และ trace `REQ-x.y` ใน test cases ให้ตรง branch ที่ขับจริง หาก filter ไม่พบ test ให้ถือว่าไม่ผ่าน ห้ามใช้ exit code 0 ของ test runner ที่ไม่พบ tests เป็นหลักฐาน

```sh
dotnet build pol-core.slnx
dotnet test tests/Pol.UnitTests/Pol.UnitTests.csproj
dotnet test tests/Pol.ArchitectureTests/Pol.ArchitectureTests.csproj
dotnet test tests/Pol.IntegrationTests/Pol.IntegrationTests.csproj
python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict
bash scripts/spec-trace.sh platform-restructure-v1
```

เมื่อจบแต่ละ task บันทึกคำสั่ง, exit code, จำนวน tests, environment, AC ที่พิสูจน์ และข้อจำกัดใน Evidence ตาม protocol แล้วผ่าน review/test gates ก่อน checkpoint บน feature branch งานที่ไม่ได้รันต้องระบุว่าไม่ได้รัน

## วิธีดำเนินงานและเงื่อนไขภายนอก

ทำต่อใน task เดิมตามลำดับเพื่อรักษาบริบท ไม่สร้าง parallel sessions โดยอัตโนมัติ งาน migration เป็นโค้ดภายใน 4 projects เดิม ไม่เพิ่ม migration service/framework ใหม่

| Input | ต้องพร้อมเมื่อ | ถ้ายังไม่มี |
|---|---|---|
| Entra tenant/issuer/app role และ client registrations | เปิด human login ในสภาพแวดล้อมจริง | ทดสอบ local protocol ได้ แต่ปิด live capability |
| PSP account/credentials/contract sandbox evidence | เปิดช่องทางรับเงินจริง | ทดสอบ reducer/orchestration ได้ แต่ไม่แสดง method ว่า live |
| SMS vendor/API และ sender credentials | เปิด SMS dispatch จริง | เก็บ delivery แบบ blocked และระบุข้อจำกัด |
| master identity/Merchant/Sale/Branch mappings | cutover rehearsal | รายงาน conflict ไม่เดาจาก email |
| backup, recovery evidence และ production authorization | ก่อนเปลี่ยน production | ไม่ทำ production action จาก spec นี้ |

การเริ่ม implementation ไม่เท่ากับการอนุมัติ deployment ห้ามถอด compatibility ที่ยังรองรับ PSP callback/customer link ค้างเพียงเพื่อให้จำนวนไฟล์ถึงเป้าหมาย
