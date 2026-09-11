# Handoff: platform restructure Tasks 1–10 complete — aggregate gates next

> From: Codex   To: any   Date: 2026-09-11

Task 1 ถึง Task 10 complete ในระดับ implementation และ local gates บน branch `codex/platform-restructure-v1`. Aggregate audit, review, checkpoint/ship และ external readiness decision ยังเป็นขั้นถัดไปของ pipeline.

## Task Summary

Spec `platform-restructure-v1` ครอบ REQ-1 ถึง REQ-12 ตาม `tasks.md`; Task 1–10 checkbox/Evidence บันทึกแล้ว. Requirements/design คง approved ตามการอนุมัติเดิม.

## Current Status

| ส่วน | ผล |
|---|---|
| Packaging | 4 source/3 test projects; source เดิม 602 ไฟล์และ tests เดิม 293 ไฟล์ย้ายครบ |
| Baseline | 2,345 passed, failed 0, skipped 0 |
| Final runtime | 2,351 passed, failed 0, skipped 0; build warnings/errors 0 |
| Static checks | Shell 16 suites, secret scan, migration parity, rename gate และ spec trace ผ่าน |
| Python | Current-base full suite 352 ผ่าน, skipped 1; preservation 18 ผ่าน, skipped 1; alignment regression 63 ผ่าน |
| Review | ไม่พบ production/config findings ค้าง; path/config/guard regressions แก้แล้ว |
| PR #251 | [Merged](https://github.com/metrodiesign/pol-core/pull/251) เวลา `2026-09-09T15:55:43Z`; CI 4/4 `SUCCESS` |
| Task 2 focused gate | `Capability=IdentityAccess`: Unit 8, Architecture 2, Integration 19; รวม 29 passed, failed 0, skipped 0; raw `task2-access-3b.log` มี `EXIT=0` |
| Task 3 focused gate | `Capability=MerchantConfiguration`: Unit 144, Architecture 12, Integration 40; รวม 196 passed, failed 0, skipped 0; raw `task3-eligibility-pinning.log` มี `EXIT=0` |
| Task 3 SQL master/race | `PolMerchantConfigTask3Test`: migration `20260910035334_Task3MerchantMaster` applied; master/FK test 1 passed; Governance checker race 1 passed |
| Task 4 focused gate | `Capability=Registration`: Integration/Host 5 passed, failed 0, skipped 0; raw `task4-host.log` มี `EXIT=0` |
| Task 5 focused gate | `Capability=OrdersLinks`: Unit 19, Architecture 3, Integration/Host 7; รวม 29 passed, failed 0, skipped 0; raw `task5-links-host.log` มี `EXIT=0` |
| Task 6 focused gate | `Capability=CheckoutTransactions`: Unit 10, Architecture 2, Integration/Host 13; รวม 25 passed, failed 0, skipped 0; raw `task6-results.log` มี `EXIT=0` |
| Task 7 focused gate | `Capability=Notifications`: Unit 8, Architecture 25, Integration/Host 8; รวม 41 passed, failed 0, skipped 0; raw `.pipeline/platform-restructure-v1/task7-final.log` มี `EXIT=0` |
| Task 7 regressions | `Capability=Registration` 5 passed และ `Capability=CheckoutTransactions` 25 passed; raw `task7-registration-regression.log` และ `task7-checkout-regression.log` มี `EXIT=0` |
| Task 7 static | migration list/pending-model/schema drift/diff/strict/trace ผ่าน; raw `.pipeline/platform-restructure-v1/task7-static.log` มี exit 0 ทุกด่าน |
| Task 10 final gate | Unit 1,143 + Architecture 375 + Integration 996 ผ่าน; full solution 2,514 passed, failed 0, skipped 0; strict/trace 126/126, pending-model 0, schema drift 0, comparator 111/111/0/0 |
| Git | Branch `codex/platform-restructure-v1`; Tasks 1–10 implementation/local gates complete; aggregate audit/review/ship และ checkpoint commit ยัง pending |
| Base | `origin/develop=fc24dffb` มี prerequisite head `0306a6f1`; prerequisite blocker หมดแล้ว |
| Task boundary | Task 1–10 `[x]`; local implementation/verify evidence complete; aggregate audit/review/ship ยังไม่รัน |

## Task 2 handoff

Implementation และ coverage matrix อยู่ที่ [changes-task2.md](../../../.pipeline/platform-restructure-v1/changes-task2.md) และ [tests-task2.md](../../../.pipeline/platform-restructure-v1/tests-task2.md).

- SQL Server พิสูจน์ JIT race, active access uniqueness, revoked coexistence, cross-Merchant guards และ authorization lease ทั้งสอง ordering
- Local host พิสูจน์ PKCE/state/nonce redirect, registered JWK `private_key_jwt`, kill switch, OpenIddict refresh/logout, CSRF และ Account-self/Platform/Merchant authorization
- `git diff --check`, strict contract และ spec trace ผ่านทั้งหมด 126 criteria
- ไม่มี Entra credential หรือ external provider/JWK registration; live callback และ production readiness ยังเป็น environment deviation

## Task 3 handoff

Implementation และ coverage matrix อยู่ที่ [changes-task3.md](../../../.pipeline/platform-restructure-v1/changes-task3.md) และ [tests-task3.md](../../../.pipeline/platform-restructure-v1/tests-task3.md).

- Merchant master เพิ่ม `Branch`/`Sale` พร้อม unique code ต่อ Merchant และ composite cross-Merchant FK
- `PaymentSettingRequestContract` เป็น typed facade บน `Governance.ApprovalRequest`; stage routing/environment/credential ไม่เปลี่ยน active config และไม่มี secret payload
- Executor เดิมพิสูจน์ credential rotation, environment switch, routing activation, reject/stale cleanup และ atomic rollback
- Session start/inquiry ใช้ pinned ProviderAccount/PspConnection, credential version และ environment; emergency disable block เฉพาะ new start
- Eligibility ครอบ Merchant policy, creator/account, adapter capability/contract evidence, amount และ currency; Omise ยัง disabled จนมี contract evidence
- Task 3 focused gate รวม 196 passed, 0 failed, 0 skipped; `git diff --check`, strict contract และ spec trace ผ่าน
- ไม่มี PSP sandbox credential/contract evidence จึงไม่ประกาศ live-ready
- OpenAPI routing `409` อยู่ใน `state.md` เป็น `UNRESOLVED` ของ Task 8 และต้องปิดก่อน final ship

## Files Changed

- Source/test projects ย้ายใต้ `src/Pol.*` และ `tests/Pol.*Tests`; ลบ project wrappers เดิม
- Solution/build props, Docker/CI, migration scripts และ runbooks ใช้ project/DLL paths ใหม่
- SDD path extractor และ tests รองรับ layout เก่า/ใหม่; ไม่เปลี่ยน comparator/scope policy
- [packaging-inventory.md](packaging-inventory.md) เก็บ callers, DI/generated wiring, jobs/config และ pending-data anchors
- Task 2 source/test ครอบ `src/Pol.*` และ `tests/Pol.*Tests` ตาม changes evidence; รายการเต็มอยู่ใน [changes-task2.md](../../../.pipeline/platform-restructure-v1/changes-task2.md)

## Important Decisions

- คง business namespaces, routes, schema, migration IDs และ runtime contexts เดิมจน task ถัดไป
- Production C# เปลี่ยนเนื้อหา 4 ไฟล์: ModuleAssemblies, PolDbContext, DesignTimeDbContextFactories และ WriteAuthorizers
- EF เลือก mapping ด้วย module marker namespace; Domain ใช้ BCL, Contracts อยู่ Application
- Config/launch settings และ runsettings อยู่ project root; config 4 ไฟล์ตรง baseline ทุก byte
- ถอด source/test compatibility symlinks แล้ว; ignored local development config ไม่เข้า Git

## Constraints

- ไม่ผ่อน comparator หรือ SDD scope เพื่อให้ diff ผ่าน
- การ commit/push ให้ `gitops` ดำเนินการตาม authorization และ workflow ของ repo
- ใช้ local SQL Server 17.0.4045.5 และฐานแยก `PolPackagingTask1Test`; ไม่ reset ฐาน dev หรือทำ production cutover
- ลง schema เดิมในฐานทดสอบใหม่และรัน fixture writes/migrations เท่านั้น ไม่ใช่ business-data migration

## Tests Run

คำสั่ง runtime current-base รอบสุดท้ายที่รันจริง:

```sh
NUGET_HTTP_CACHE_PATH=/tmp/pol-task1-nuget-http-cache dotnet build pol-core.slnx
set -a
source .env.integration
set +a
export POL_DB=PolPackagingTask1Test
export NUGET_HTTP_CACHE_PATH=/tmp/pol-task1-nuget-http-cache
dotnet test pol-core.slnx --logger 'trx;LogFilePrefix=task1-final-verify-retry' --results-directory .pipeline/platform-restructure-v1/task1-verify-trx-retry
```

ผล: Unit 1,077, Architecture 355, Integration/Host 919 ผ่านทั้งหมด ไม่มี skipped; TRX ยืนยันรวม 2,351 passed, failed 0, skipped 0

| คำสั่ง | ผล |
|---|---|
| `git diff --check` และ `git ls-files -u` | exit 0 และไม่มี unmerged paths |
| `python3 scripts/ci-workflow-preservation.py --base fc24dffb --sdd-scope` | `untouched`, exit 0 |
| `python3 scripts/tests/test_ci_workflow_preservation.py` | 18 ผ่าน, skipped 1, exit 0 |
| `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` | 352 ผ่าน, skipped 1 |
| `python3 scripts/repo_policy_alignment.py --check --json` | `verdict=allow`, exit 0 |
| `python3 scripts/tests/test_repo_policy_alignment.py` | 63 ผ่าน, exit 0 |
| `bash docker/entrypoint.test.sh` | 62/0 |
| `bash docker/migrate-entrypoint.test.sh` | 59/0 |
| `bash .ai/bin/check-secrets.sh --all` | exit 0 รวมไฟล์ย้ายใหม่ที่เป็น intent-to-add |
| `scripts/check-migration-script.sh` | schema ตรง EF migrations, exit 0 |
| `bash scripts/check-rename-identifiers.sh` | exit 0 |
| `python3 scripts/spec_contract.py check --feature platform-restructure-v1 --strict` | 126 เกณฑ์อ้างครบ, exit 0 |
| `bash scripts/spec-trace.sh platform-restructure-v1` | 126 เกณฑ์อ้างครบ, exit 0 |

Shell suites ตาม CI ทั้ง 16 ชุดผ่าน รายละเอียด local อยู่ `.pipeline/platform-restructure-v1/tests-task1.md` และ `shell-task1-results.json`

## Known Issues

Policy blocker เดิม resolved: PR #251 merge แล้ว, base `fc24dffb`, conflict 0, SDD scope `untouched` และ current-base policy suites ผ่านทั้งหมด

Runtime รอบแรก SQL connection refused เพราะ local compose down ผู้ตรวจหยุด process ของตัวเอง เริ่ม SQL compose ตาม runbook แล้ว retry หนึ่งครั้งผ่าน Wrapper ของ full solution detached ก่อนพิมพ์ final `EXIT=` แต่ project summaries และ TRX ทั้งสามไฟล์เป็น green

ยังไม่ได้ build/run Docker images หรือทดสอบ Entra/PSP/SMS จริง; ไม่ประกาศ cutover-ready และยังไม่มี final aggregate audit/review/ship

## Task 4 handoff

Task 4 ปิด REQ-4.1–REQ-4.12 ด้วย target Account/Access registration lifecycle, SQL Server decision race/atomicity และ host applicant/reviewer isolation.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task4.md`
- Test matrix/evidence: `.pipeline/platform-restructure-v1/tests-task4.md`
- Focused Registration: 5 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task4-host.log` มี `EXIT=0`
- MerchantConfiguration regression: 196 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task4-task3-full-regression.log` มี `EXIT=0`
- Migration chain: Task3→Task4 history/FK ownership ผ่านบน `PolRegistrationTask4ChainTest`; raw `.pipeline/platform-restructure-v1/task4-migration-chain.log`
- Host route: applicant public redaction/isolation และ reviewer permission/scope ผ่าน; Task4 event registry publishable แล้ว แต่ Notification materialization เป็น Task7

## Task 5 handoff

Task 5 ปิด REQ-6 และ REQ-7.1–7.5 ด้วย Order workflow, trusted pricing/owner resolution, hashed PaymentLink, protected replay, anonymous capability และ customer-safe summary โดยไม่มี Payment หรือ Transaction aggregate.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task5.md`
- Test matrix/evidence: `.pipeline/platform-restructure-v1/tests-task5.md`
- Focused gate: `Capability=OrdersLinks` = Unit 19, Architecture 3, Integration/Host 7; รวม 29 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task5-links-host.log` มี `EXIT=0`
- SQL evidence: fresh `PolOrdersLinksTask5Test` ตรวจ Task2→Task5 chain, legacy backfill, direct Order FK, one active link, protected replay, rotate guard และ no Payment/Transaction table; cancel guard ผ่าน unit handler
- Host evidence: `/api/v1/checkout/access` รับ token ผ่าน body และ set Secure/HttpOnly cookie; `/api/v1/checkout/summary` ใช้ Order-only Data Protection capability พร้อม cookie/proof binding และ redaction
- Migration: `20260910060757_Task5OrdersLinks` เป็น forward migration เดียวหลัง Task4; `docker/migrations/schema.sql` drift gate ผ่าน
- Environment deviation: ไม่มี live Entra/PSP/SMS credential; local SQL Server/Data Protection protocol evidence ใช้ตัดสินแทน และไม่ประกาศ production/cutover ready

## Task 6 handoff

Task 6 ปิด REQ-7.6–7.10 และ REQ-8.1–8.12 ด้วย `Transaction`/`TransactionEvent`, confirm ที่ผูก capability ต่อ tab, persist-before-PSP, shared `OrderSerializationGate`, callback/return/inquiry reducer และ status-only return context.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task6.md`
- Test matrix/evidence: `.pipeline/platform-restructure-v1/tests-task6.md`
- Focused gate: `Capability=CheckoutTransactions` = Unit 10, Architecture 2, Integration/Host 13; รวม 25 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task6-results.log` มี `EXIT=0`
- Task 5 regression: `Capability=OrdersLinks` = Unit 19, Architecture 3, Integration/Host 7; รวม 29 passed, 0 failed, 0 skipped; raw `.pipeline/platform-restructure-v1/task6-task5-regression.log` มี `EXIT=0`
- SQL evidence: fresh `PolCheckoutTransactionsTask6Test` ใช้ migration chain Task2→Task6; capture adapter อ่านแถว Transaction/snapshot ที่ commit แล้วผ่าน connection แยก, two-tab race สร้าง charge ได้หนึ่งแถว/หนึ่ง call, confirm/cancel ใช้ lock เดียวกัน, result reducer คง canonical pointer, outbox เดียว, duplicate evidence, late success และ mismatch semantics
- Migration/static evidence: `Task6Transactions` อยู่ถัดจาก `20260910060757_Task5OrdersLinks`; migration drift, pending-model, `git diff --check`, strict contract และ spec trace ผ่าน 126 criteria
- Security boundary: callback/return/inquiry/admin verify ใช้ shared reducer และ provider pin เดิม; return binding ให้ status-only access และ GET status ไม่เรียก PSP; ไม่มี PAN/card/QR fields
- Environment deviation: ไม่มี live PSP credentials หรือ provider sandbox contract จึงใช้ local SQL Server, capture adapters และ local contract/signature evidence; ไม่ประกาศ live/cutover readiness

## Task 7 handoff

Task 7 ปิด REQ-9.1–9.10 ด้วย Commerce-owned Notification/Delivery runtime และ explicit control-outbox → Commerce-inbox handoff.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task7.md`
- Test matrix/evidence: `.pipeline/platform-restructure-v1/tests-task7.md`
- Focused gate: `Capability=Notifications` = Architecture 25, Unit 8, Integration/Host 8; รวม 41 passed, 0 failed, 0 skipped; raw `task7-final.log` มี `EXIT=0`
- Regression: `Capability=Registration` 5 passed และ `Capability=CheckoutTransactions` 25 passed; raw `task7-registration-regression.log` และ `task7-checkout-regression.log` มี `EXIT=0`
- SQL evidence: fresh `PolNotificationsTask7Test` ตรวจ inbox/notification/delivery atomicity, `SourceEventId` dedupe, Email/SMS fan-out, accepted/delivered/unknown/blocked states, immutable recipient/template/endpoint snapshots, append-only attempts, exact retry schedule และ manual queue
- Webhook evidence: Control Plane endpoint/secret ถูกอ่านผ่าน `IBusinessWebhookConfigurationReader`; HTTPS/443, DNS public-range, HMAC, resolved-IP pin, no proxy/cookies/redirect และ 302 `redirect_rejected` ผ่าน local capture; no live endpoint call
- Operations evidence: search `OrderNo`/`TransactionNo`/`CorrelationId` และ Merchant isolation ผ่าน; retry/note ไม่เปลี่ยน `Order.PaymentStatus` และ note append-only
- Migration/static evidence: `20260910094927_Task7NotificationRuntime` และ `20260910101508_Task7WebhookEndpointUniquenessLive` อยู่หลัง Task6; migration list, pending-model, schema drift, `git diff --check`, strict contract และ spec trace ผ่านใน `task7-static.log`
- Environment deviation: ไม่มี live SMS vendor/credential หรือ external provider contract; SMS capability คง `BLOCKED_NOT_CONFIGURED`; ไม่มี live DNS rebinding harness จึงใช้ re-resolution/pinning code path และ local adversarial capture แทน
- Historical pause state: Task7 เคยหยุดตาม user request; Task8–10 ถูกทำต่อจน local gates ครบใน handoff นี้

## Task 8 handoff — C1 bounded slice

Task8 C1 เพิ่ม canonical API-081, API-082, API-085, API-091 และ API-095–099 ใน `CanonicalCommerceEndpoints`. Child/order history/transaction reads ตรวจ Order parent และ Merchant scope ก่อนอ่าน; Transaction verify ใช้ pinned context และ review note เป็น append-only event.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task8.md` ส่วน `Slice C1`
- Test matrix/evidence: `.pipeline/platform-restructure-v1/tests-task8.md` ส่วน `Slice C1 evidence`
- SQL/HTTP gate: `Task8CommerceC1SqlTests` 1 passed บน `PolCommerceC1Task8_202609102120`; ครอบ patch/trusted repricing, items/history, checkout methods zero-provider read, transaction IDOR, verify, note replay และ stale `412`
- Regression: `Capability=OrdersLinks` 29 passed และ `Capability=CheckoutTransactions` 25 passed; Task5 Data Protection fixture แก้ให้ใช้ wall clock จริงโดยไม่แตะ production replay semantics
- Comparator: EndpointDataSource actual 267, overlap 100, missing 11, deferred 0; remaining API-103–107 และ API-111–116 เป็น Notification/Operations work
- Migration: forward `20260910124500_Task8CommerceRuntimeGrants` ต่อจาก `20260910121500_Task8RuntimeGrants`; fresh history/effective `pol_app` permissions, EF pending-model และ schema drift ผ่าน
- Boundary: Task8 ยังไม่ complete; ไม่ทำ C2, Task9 cutover หรือ Task10 legacy retirement ใน slice นี้

## Task 8 final handoff

Task8 ปิด canonical API inventory และ operations guide แล้ว: `api-scope.json` v1 111 rows ผ่าน OpenAPI/EndpointDataSource comparator, deferred API-033/034/108/109/110 absent, และไม่มี duplicate canonical method/path semantics.

- Implementation summary: `.pipeline/platform-restructure-v1/changes-task8.md` ส่วน C2 และ final-wide; source C1/C2 canonical endpoints, notification receipt/operations, health metadata และ write-floor/gateway fixes
- Matrix: `.pipeline/platform-restructure-v1/task8-operation-matrix.md`; metadata proof แยกจาก runtime/schema proof, legacy extras167 อยู่ `.pipeline/platform-restructure-v1/task8-legacy-extras.md`
- Operations guide: `docs/runbooks/platform-api-v1.md` ภาษาไทย ครอบ auth contexts, CSRF/Idempotency/ETag, errors, SFS, callbacks, health และ deviations
- Focused final: `Capability=ApiOperations` Integration 51 passed; raw `.pipeline/platform-restructure-v1/task8-final.log`
- Regression: `IdentityAccess` 29, `OrdersLinks` 29, `CheckoutTransactions` 25, `Notifications` 41 ผ่าน
- Static: migration list/pending/drift, named OpenAPI 5, SimpleRouting, diff, strict contract และ trace ผ่าน; raw `.pipeline/platform-restructure-v1/task8-final-static.log`
- Comparator: actual278, canonical overlap111, missing0, deferred0; legacy extrasยังอยู่เพื่อ Task10
- Corrective isolation: xUnit collection `ApiOperationsSql` (`DisableParallelization=true`) ครอบ SQL classes ที่ใช้ AppConn/SaConn; two consecutive full runs `task8-final-corrective-run1b.log` และ `task8-final-corrective-run2.log` ได้ 51/51. RED12 เดิมเก็บชื่อ failure ไม่ครบจาก minimal runner จึงไม่อ้าง exact root; proven overlap เป็น shared mutable AppConn/SaConn setup/cleanup และ collection isolation ปิด class-level interference โดยไม่ serialize concurrency ภายใน handler
- Environment/deviation: ใช้ local SQL Server `PolOperationsC2Task8_202609102345`, capture provider verifier/sender และ `BLOCKED_NOT_CONFIGURED`; ไม่มี live Entra/PSP/SMS authorization และไม่ประกาศ cutover-ready
- Boundary: Task9 เป็นงานถัดไปตาม dependency; Task8 ไม่ทำ migration cutover, production action, commit หรือ legacy retirement

## Task 9 current handoff

Task 9 Migration Readiness complete ในระดับ implementation/local tests: deterministic identity/ID mapping, conflict report, pending/rejected registration mapping, session revoke, session-to-Transaction history/provenance, actual target-owner backfill จาก synthetic backup→restore, global transaction-scoped SQL writer lease, one-writer pause/watermark recovery, forward-safe rollback และ durable isolated SQL tables อยู่ใน `changes-task9.md`/`tests-task9.md`.

- `Capability=MigrationReadiness`: Unit 7 และ SQL Integration 4 ผ่าน, failed 0, skipped 0; final bundle raw logs อยู่ใน `.pipeline/platform-restructure-v1/task9-final.log` และ `task9-static.log`
- rename-guard follow-up: test helper/calls `PaymentSession(...)` เปลี่ยนเป็น `LegacySession(...)`; exact Task9 test class 7 passed; raw `.pipeline/platform-restructure-v1/task9-rename-followup.log`; gitops ต้อง rerun temporary-index candidate scan
- migration `20260910140000_Task9MigrationReadiness` อยู่ใน list และ `has-pending-model-changes` เป็นศูนย์
- synthetic backup/local SQL เป็นหลักฐาน machinery เท่านั้น; sanitized backup, master identity mapping, external credentials และ production authorization ยังขาด จึงไม่ประกาศ production/cutover ready
- Task 8 canonical API/legacy extras คงเดิม; Task 10 เท่านั้นที่ตรวจ consumer/callback แล้ว retire legacy

## Task 10 current handoff

Task10 implementation/local architecture closure อยู่ใน `.pipeline/platform-restructure-v1/changes-task10.md`, `.pipeline/platform-restructure-v1/tests-task10.md` และ `task10-retirement-inventory.md`.

- Runtime DI เหลือ `ControlPlaneDbContext` + `CommerceDbContext`; old context namesเหลือเฉพาะ test compatibility aliases และไม่ถูก register
- `PolDbContext` ไม่ถูกสร้างจาก API startup; local development ใช้ `scripts/dev-db-migrate.sh` explicit operator command
- Current config scan รวม module/runtime source (ตัด immutable migration Designer) พบ 94 physical mappings และ duplicate 0; migration relational overlay คง Task9 FK/index/check semantics
- Idempotency owner แยก CP `Governance.OperationRecord` กับ Commerce `AdminOperationRecord`; replay options รองรับ Money amount/currency และ raw lease ใช้ conditional SQL transaction seam โดยไม่มี EF projection
- Final full solution: Unit 1,143, Architecture 375, Integration 996, รวม 2,514 passed, failed 0, skipped 0; strict/trace, pending-model, schema drift, diff check และ route comparator 111/111/0/0 ผ่าน
- External readiness ยัง outstanding: sanitized backup/master mapping, Entra/PSP/Email/SMS evidence และ production authorization ไม่มีใน environment นี้; remaining legacy rows KEEP/DEFER ตาม inventory และไม่มี cutover

## Task 10 follow-up — policy alignment

Independent verification พบ documentation/policy drift หลัง Task10: source modules Access, Accounts, Checkouts, Migration และ Platform ไม่อยู่ใน registry, runtime narrative อ้าง historical context names และ isolation checker ใช้ path เก่า. แก้เฉพาะ `.ai/shared/ARCHITECTURE.md`, `scripts/repo_policy_alignment.py` และ `scripts/tests/test_repo_policy_alignment.py`.

- `repo_policy_alignment.py --check --json`: exit 0, verdict allow, diagnostics 0
- `test_repo_policy_alignment.py`: 64 passed, 0 failed
- Mutation fixtures ยังคง RED สำหรับ third context, missing Commerce context, unsealed guard, missing filter, stale module row และ mixed layout
- Runtime suite ไม่ rerun เพราะ follow-up เป็น policy/docs/test-only; existing full solution evidence remains authoritative

## Deviation: canonical Order create/draft chain

หัวข้อนี้ประกาศการเบี่ยงจาก approved design ของ REQ-6/REQ-7 อย่างเปิดเผย (must-fix 1 ทางเลือก ก
ตาม review รอบ aggregate) ผู้ตัดสินว่ารับได้หรือไม่คือ user

### ข้อเท็จจริง (ยืนยันด้วย refute pass 2 lens: trace + contract)

- **API-079** (`POST /api/v1/orders`, `src/Pol.Api/Api/Program.cs:1964`) รับ `CreateOrderFromCartRequest`
  (`CartId`, `Customer`, `PaymentMethod`, `MerchantId?`, `OriginatorId?`) แล้วลงที่ `Order.Create`
  ซึ่งตั้ง `Status = OrderStatus.Pending` ไม่ใช่ `Draft`; นี่คือ operation เดียวที่ contract ผิดจาก design จริง
  (contract test `ApiOperationsContractTests.cs:138` pin ตาม implementation ไม่ใช่ตาม design)
- design กำหนด `POST /api/v1/orders` รับ `CreateOrderRequest` (`businessType`, `currency`, `items[]`,
  `issueNow` default true; `issueNow=false` -> DRAFT ไม่มี link) — type `CreateOrderRequest` **ไม่มีอยู่ใน `src/` เลย**
- ผู้ผลิต `OrderStatus.Draft` ใน production มีจุดเดียวคือ `Order.CreateDraft` (`Order.cs:265`) ซึ่งมี caller
  ใน `src/` เพียง `CreateOrderHandler` (`OrderWorkflow.cs:424`) และ `CreateOrderHandler` ไม่มีใครสร้าง
  `CreateOrderCommand` ให้ (0 จุดใน `src/`) โดยถูกตรึงด้วย arch test
  `PaymentAuthorizationArchitectureTests.cs:22` (`Assert.Empty` ของ `new CreateOrderCommand(`)
- ผลคือ API-081 (PATCH draft), API-083 (issue), API-087 (rotate payment-link) คืน 409/InvalidOperation
  เสมอเมื่อยิงกับ order ที่สร้างได้จริง (Pending) และไม่มี production path ใด mint `PaymentLink`
  (`_issuer.Issue` มี 3 call site ใน `src/` ทั้งหมดอยู่ใน handler ที่ unreachable) จึง `POST /checkout/access`
  และ `POST /checkout/confirm` ไม่มี input จริง

### สิ่งที่ส่งมอบจริงในรอบนี้

- canonical draft/issue/link chain ส่งมอบครบ **ระดับ application layer** (`CreateOrderHandler`,
  `IssueOrderHandler`, `RotatePaymentLinkHandler`, `OrderLinkIssuer`, `PaymentLinkReplayService`)
  พร้อม test ระดับ handler และ SQL integration ที่เรียก handler ตรง (`Task5OrdersLinksSqlIntegrationTests`)
- `POST /api/v1/orders` คงพฤติกรรม cart-based ของเดิม (Pending)

### สิ่งที่ยังไม่ส่งมอบ (หนี้ที่ประกาศ)

- HTTP branch `CreateOrderRequest`/`issueNow` ตาม design
- producer ผ่าน HTTP ของ API-081/API-083/API-087 (ปัจจุบัน 409 เสมอ)
- production path ที่ mint `PaymentLink` (checkout จึงไม่มี input จริงจาก production flow)

### ทางเลือก (ข) wire ให้ครบ = follow-up spec แยก (ยังไม่ทำในรอบนี้)

ต้องเพิ่ม `CreateOrderRequest` เป็น branch ของ `POST /orders` (หรือ operation แยก) ที่เรียก
`CreateOrderHandler`, ปลด arch pin `PaymentAuthorizationArchitectureTests.cs:22`, และเปลี่ยน contract pin
`ApiOperationsContractTests.cs:138` ให้ตรง design; ต้องผ่าน must-fix 2 (`SummaryToken` sentinel, แก้แล้วในรอบนี้)
ก่อนเสมอ เพราะการ wire draft สองใบขึ้นไปในฐานเดียวกันจะชน filtered unique index ทันทีถ้ายังเขียน sentinel
งานนี้เป็นการ rewire money path กลาง จึงต้องเป็น follow-up spec ที่ user อนุมัติ ไม่ทำในรอบ rework สุดท้าย

## Next Steps

1. ทำ aggregate audit และ verify บน diff รวม โดยคง Task10 KEEP/DEFER inventory เป็นข้อจำกัด
2. ให้ review ตัดสินคุณภาพและ AC coverage ก่อน ship
3. ให้ gitops จัดการ checkpoint/ship ตาม authorization; external readiness gap ต้องคงอยู่จนมี sanitized backup, mapping และ production authorization จริง

## ผลตรวจ current-base

วันที่ 2026-09-09 ยืนยัน PR #251 `MERGED`, base `fc24dffb` และ conflict 0 Current-base gate ผ่าน diff check, no-unmerged, SDD scope `untouched`, preservation 18, full Python 352, alignment 63, build 0 warnings/errors, runtime 2,351 และ strict/trace 126

รายละเอียด command/output อยู่ `.pipeline/platform-restructure-v1/tests-task1.md` Task 1 จึง complete โดยคง caveat เรื่อง detached wrapper และ SQL compose retry ไว้ตรง ๆ
