# Implementation Tasks: การตั้งค่าผู้ให้บริการรับชำระเงินของร้านค้า

> Status: approved 2026-09-06

> แต่ละ task เป็น vertical slice ที่ตรวจได้แยกกัน Backend tasks ใช้ primitives และ schema ร่วมกันสูง
> จึงควรทำตามลำดับใน session เดียว ส่วน Admin Console อยู่ repository `pol-admin` และทำหลัง API contract คงที่

- [x] 1. ฐานความปลอดภัยสำหรับ vault, authorization lease และ approval execution — แก้ staged-secret expiry ให้ active/retired version อ่านได้, เพิ่ม `MerchantRuntimeAuthorizationLease` พร้อม architecture guard, เพิ่ม durable `ApprovalExecutionRecord` และแยก in-transaction success audit จาก external tamper-resistant denial telemetry; done เมื่อ credential ไม่หมดอายุหลัง 24 ชั่วโมง, revoke race ปิด และ decision replay ไม่ execute ซ้ำ
     Satisfies: REQ-1.5-REQ-1.8, REQ-2.8-REQ-2.10, REQ-4.4-REQ-4.6, REQ-8.4-REQ-8.6, REQ-9.1-REQ-9.2, REQ-9.5, REQ-9.9-REQ-9.11
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 0 warnings / 0 errors
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration' --logger trx --results-directory /tmp/merchant-psp-task1-full-pass-20260906-1916` -> 2,031 passed / 0 failed / 0 skipped ใน 13 projects
       - viewports: n/a — logic-only
       - deviations: ย้ายเฉพาะ expand migration ของ `txn.ApprovalExecutionRecords` จาก task 9 มา task 1 เพื่อให้ durable ledger ใช้งานได้และ migration snapshot ตรง model; legacy vault cleanup, backfill และ compatibility cutover ยังอยู่ task 9

- [x] 2. Merchant environment และ PSP connection control plane แบบครบเส้นทาง — เพิ่ม `Merchant.PaymentEnvironment`, active/pending secret environment, zero-method connection, environment-aware adapter calls, provider field allowlist/size limit/no-store, safe callback/hint views, permission matrix, ETag/idempotency/CSRF และ active credential test; done เมื่อสร้าง 2C2P/Omise connection ได้หนึ่ง record ต่อ provider, environment สืบทอดจาก Merchant และ API ไม่คืนหรือ log secret
     Satisfies: REQ-1, REQ-2.1-REQ-2.6, REQ-3, REQ-4, REQ-7.1-REQ-7.7, REQ-9.1-REQ-9.10, REQ-11.1
     Depends on: 1
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 53 projects, 0 errors / 0 warnings
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration' --logger trx` -> 2,063 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 693, Architecture.Tests 307, Payments.Tests 284, Merchants.Tests 183)
       - test: migration `20260906123608_MerchantPaymentEnvironment` + `./scripts/check-migration-script.sh --write` -> schema.sql regenerated; default `PaymentEnvironment`/`ActiveSecretEnvironment` = 1 (sandbox), CHECK `CK_Merchants_PendingPaymentEnvironment`
       - viewports: n/a — logic-only
       - deviations: REQ-2.6 (unknown environment -> 400) พิสูจน์ที่ `PspEnvironments.FromCode` (ArgumentException -> 400) เพราะ endpoint ที่รับค่า environment คือ environment-change request ของ task 7; `PspOptions.UseSandbox` ยัง bind อยู่แต่ adapter ไม่อ่านแล้ว (ตัดใน task 9 หลัง bootstrap); `IPspAdapter.CallbackUrlFor` เพิ่มเป็น default member (route-only) เพื่อให้ store คืน callback URL โดยไม่อ้าง `PspOptions` ข้าม assembly; 16 KiB limit บังคับด้วย bounded body read ใน endpoint (`ReadSecretBodyAsync`) เพราะ minimal-API binding เกิดก่อน filter

- [x] 3. Verified provider capabilities และ policy สองระดับ — ให้ `IPspAdapter.SupportedMethods` หมายถึง method ที่มี sandbox evidence, ปิด Omise ทุก method จน dependency spec ผ่าน, คง 2C2P สาม method, แยก account method จาก Merchant method และคืน effective denial reason จาก backend; done เมื่อ implementation ที่ยังไม่พิสูจน์เปิดไม่ได้, CSV/JSON ไม่เป็น authorization source และ connection ที่ไม่มี method ยังจัดการ credential ได้
     Satisfies: REQ-5, REQ-6.6-REQ-6.7, REQ-6.14-REQ-6.16
     Depends on: 2
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 53 projects, 0 errors / 0 warnings
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2,066 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 696, Architecture.Tests 307, Payments.Tests 284, Merchants.Tests 183)
       - test: mutation-check 4 guard ของ task นี้ (ลบ `ActiveSecretVersionId is null` guard, ลบ `adapter_unverified` denial, ให้ `accountEnabled` fallback ไป CSV `connection.Supports`, ลบ adapter clause ใน `EnsureAccountMethodCanEnable`) -> `AdminPspConnectionControlPlaneTests` แดงทุกครั้ง (2, 2, 1 และ 1 failure ตามลำดับ) แล้วเขียวหลัง restore
       - test: REQ-5.2/REQ-5.11 พิสูจน์บน adapter จริงไม่ใช่ fake — `TwoCTwoPAdapterTests.SupportedMethods_declares_the_three_channels_it_has_a_channel_code_for` (สาม method) และ `OmiseAdapterTests.SupportedMethods_is_empty_until_sandbox_evidence_exists` (ว่าง) -> ผ่านทั้ง 2 test ในชุด Payments.Tests 284 passed ข้างบน
       - viewports: n/a — logic-only
       - deviations: REQ-5.15 (ต้องเปิดทั้ง account method และ merchant policy) พิสูจน์ที่ `EffectivePaymentCapabilityResolver` ผ่าน `EffectivePaymentCapabilityResolverIntegrationTests` ซึ่งเป็น `Category=Integration` จึงไม่อยู่ในตัวเลขข้างบน; REQ-5.6/5.7 (ไม่เลือก connection ที่ปิด และตรึง route ของ session เดิม) เป็นเส้นทาง create-session ที่อยู่ใน task 4; `EffectivePaymentCapabilityResolver.ResolveLegacyAsync` ยังอ่าน CSV แต่เป็น `PaymentAuthorizationMode.LegacyRead` ของ cutover ที่ task 9 เป็นเจ้าของ — path NormalizedRead ที่ใช้จริงอ่าน normalized rows อย่างเดียว; `MethodPayableHandler` ยังใช้ `connection.Supports` + `DefaultPspSelection` และจะถูกแทนด้วย server-side routing ใน task 4

- [x] 4. Server-side routing authority และ immutable Payment Session snapshot — ย้าย PSP selection เข้า `CreateSessionHandler` ใต้ Merchant shared lock, คืน `PspRouteSelection`, ตรึง connection/secret/environment, ใช้ primary/fallback เฉพาะก่อน PSP call, recheck emergency kill ก่อน first claim และเพิ่ม compatibility request ที่รับแต่ไม่ใช้ legacy `Psp`; done เมื่อทุก audience ใช้ routing เดียว, timeout ไม่ failover และ settings change ไม่เปลี่ยน attempt เดิม
     Satisfies: REQ-2.7-REQ-2.10, REQ-3.5, REQ-5.6-REQ-5.7, REQ-5.15, REQ-6.8-REQ-6.18
     Depends on: 1, 2, 3
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 53 projects, 0 errors / 0 warnings
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2,063 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 696, Architecture.Tests 315, Payments.Tests 273, Merchants.Tests 183)
       - test: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter 'FullyQualifiedName~PaymentAuthorizationLockIntegrationTests'` -> 2 passed / 0 failed (AC-4.8 `Session_creation_shared_lock_waits_for_an_environment_activation_exclusive_lock` ขับ shared/exclusive applock จริงบน SQL Server :11433)
       - test: migration `20260906143227_PaymentSessionRoutingSnapshot` + `./scripts/check-migration-script.sh --write` -> schema.sql regenerated; 4 snapshot columns (`PspConnectionId`/`SecretVersionId`/`PspEnvironment` NULL, `RoutingSnapshotVersion` tinyint NN default 0) + CHECK `CK_PaymentSessions_RoutingSnapshotV1`; ไม่มี Drop+Add แทน Rename; ไม่มีตารางใหม่จึงไม่ต้อง GRANT
       - test: mutation-check guard `EligibleAsync` ใน route selector (ลบเงื่อนไข `connection.ActiveSecretEnvironment != merchantEnvironment`) -> `Skips_a_connection_whose_environment_differs_from_the_merchant` แดง, restore แล้วเขียว
       - viewports: n/a — logic-only
       - deviations: REQ-5.15 (account + merchant policy) พิสูจน์ผ่าน `IEffectivePaymentCapabilityResolver` ที่ route selector เรียก; join จริงเป็น SQL-Server-only จึงยังพิสูจน์เต็มใน `Category=Integration` (task 3 ตั้ง precedent) — unit test ของ selector ใช้ fake resolver ขับ branch primary-denied->fallback; AC-4.3 ส่วน webhook verify ใช้ pinned secret เป็นของ task 8 (task 4 ครอบเฉพาะ PSP call ฝั่ง create/redirect); การ insert session พร้อม CHECK บน live SQL Server + rollback ครอบใน task 9 (AC-9.7); composite FK ของ Session->connection/vault (design 564-566) ยังไม่ทำ — ไม่มี AC ของ task 4 บังคับ บันทึกใน changes.md

- [x] 5. Simple routing API ที่ป้องกัน advanced-rule overwrite — เพิ่ม GET/PUT merchant simple-routing contract, สร้างหรือแทนเฉพาะ draft ที่ไม่มี `any`, amount หรือ Originator predicates, validate primary/fallback/coverage/environment ที่ server และใช้ routing activation maker-checker เดิม; done เมื่อ stale/malicious client เขียนทับ advanced rules ไม่ได้และทุก Merchant method ที่เปิดมี primary ก่อน activation
     Satisfies: REQ-6.1-REQ-6.7, REQ-6.12-REQ-6.21, REQ-9.3-REQ-9.9
     Depends on: 1, 2, 3, 4
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 53 projects, 0 errors / 0 warnings
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2,078 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 711, Architecture.Tests 315, Payments.Tests 273, Merchants.Tests 183)
       - test: adversarial 9 แถว + AC-5.2/5.3/5.5 ที่ store boundary `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~SimpleRoutingControlPlaneTests'` -> 15 passed (advanced read-only #1-4 + pending-approval advanced, extra-field ignore #5, stale ETag #6, idempotency reuse #7, primary==fallback #8, cross-merchant/wrong-env #9, merchant-policy gate, activation coverage AC-5.4)
       - test: mutation-check ด่าน advanced guard (`IsAdvancedRule` ->ทุก clause เป็น `&&`+method sentinel) -> `Put_over_an_advanced_draft_is_read_only...`(amount/originator/any) + `Put_when_an_advanced_active_ruleset...` แดงทั้ง 4, restore แล้วเขียว
       - test: mutation-check ด่าน coverage (`EnsureRoutingCoverageAsync` enabled-method query `&& false`) -> `Activation_is_routing_incomplete_when_an_enabled_merchant_method_has_no_primary` แดง, restore แล้วเขียว
       - test: OpenAPI + permission pin `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~SimpleRoutingControlPlaneTests.OpenApi_pins_the_simple_routing_get_and_put_contracts|FullyQualifiedName~PermissionGateSitesTests'` -> ผ่าน (GET/PUT operationId + ETag + If-Match/Idempotency-Key; 160 gate sites, ทั้งสอง admin/settings.manage)
       - viewports: n/a — logic-only (pol-admin หน้าตั้งค่าอยู่ task 10 นอก scope run นี้)
       - deviations: ไม่มี migration/ตารางใหม่ — simple-routing reuse `txn.RoutingRulesets`/`txn.RoutingRules` ของ task 1-3 จึงไม่ต้อง `check-migration-script.sh` และไม่ต้อง GRANT; coverage guard AC-5.4 (REQ-6.17) ใส่ใน `RequestActivationAsync` ซึ่งเป็น activation path เดียวที่ทั้ง simple และ advanced ใช้ร่วมกัน; AC-5.3 join account+merchant policy จริงเป็น SQL-Server-only เหมือน task 3/4 — unit ที่ store boundary ขับ branch ด้วย SQLite (MerchantPaymentMethods + normalized account rows); canonical repo-wide transaction-inventory doc (`design.md §Transaction inventory`) มีแถว simple-routing set ค้างเพิ่มเชิงเอกสาร (enforced gate = dict ใน `TransactionInventoryTests` อัปเดตแล้ว 12->13)

- [x] 6. Credential rotation และ optional candidate test — stage candidate พร้อม target environment, เพิ่ม read-only candidate-test endpoint ที่ compare pending/version หลัง probe, ให้ maker-checker activate/reject โดยไม่บังคับ test, reset active health หลัง activation และรักษา retired version ที่ Session อ้าง; done เมื่อ maker อนุมัติเองไม่ได้, pending ซ้ำได้ 409, reject cleanup ครบ และ candidate test ไม่เปลี่ยน active state
     Satisfies: REQ-2.8-REQ-2.10, REQ-7.8-REQ-7.11, REQ-8, REQ-9.3-REQ-9.11
     Depends on: 1, 2, 4
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror` -> ผ่าน, 53 projects, 0 errors / 0 warnings
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2,085 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 716, Architecture.Tests 317, Payments.Tests 273, Merchants.Tests 183)
       - test: candidate-test + guard ที่ store boundary `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~AdminPspConnectionControlPlaneTests'` -> 17 passed (AC-6.2 probe candidate/target-env/ไม่แตะ active health, compare-after-probe critical #18 ไม่เขียน stale, probe fail 502+probe_failed, AC-6.1 approval_pending, AC-7.6 env-pending -> approval_pending)
       - test: executor AC-6.3/6.5/6.6 `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~AdminPaymentsApprovalExecutorTests'` -> 5 passed (approve = retire active(ExpiresAt NULL)+activate candidate+reset health, retired version ยังอ่านได้ผ่าน vault, candidate หมดอายุ -> discard + outcome credential_candidate_expired)
       - test: mutation-check 4 ด่าน -> ลบ Retired ออกจากด่าน not-readable ของ `ReadVersionForServerAsync` (AC-6.5 test แดง), mute `EnsureVersion` ใน `TestCandidateCredentialAsync` (compare-after-probe test แดง), `candidateExpired && false` (expiry test แดง), `PendingApprovalId is not null && false` (approval_pending test แดง) -> ทั้ง 4 แดงแล้วเขียวหลัง restore
       - test: maker-checker (AC-6.3) พิสูจน์ที่ governance decision layer เดิม `GovernanceDomainTests.Maker_cannot_decide_own_request` + `GovernanceStoreTests` (maker_cannot_decide) -> ผ่านในชุด Governance.Tests 8 passed
       - viewports: n/a — logic-only (pol-admin UI อยู่ task 10 นอก scope run นี้)
       - deviations: ไม่มี migration/ตารางใหม่ — pending fields (`PendingSecretEnvironment`/`PendingSecretTestResult`/`PendingSecretTestedAt`) มีครบจาก migration `20260906123608_MerchantPaymentEnvironment` ของ task 2 แล้ว จึงไม่ต้อง `check-migration-script.sh` และไม่ต้อง GRANT; AC-6.6 (candidate หมดอายุ -> `credential_candidate_expired`) surface เป็น execution-failed outcome ของ approval executor ไม่ใช่ 409 sync เพราะ approve เป็น async ผ่าน governance outbox (ตรงกับ design error map "execution failed `credential_candidate_expired`"); AC-6.7 lease verify ครอบ create (task 2 เดิม) + candidate-test (task นี้) — approve executor เป็น governance-driven async ตรวจ scope+permission+maker-checker ที่ `GovernanceStore.DecideAsync` แต่ไม่ re-verify merchant-runtime lease ใน executor (task 2 shipped behavior, ไม่มี AC-6 บังคับเปลี่ยน executor signature); AC-6.5 retired-readable พิสูจน์ที่ vault seam (ไม่ผ่าน webhook ซึ่งเป็น task 8)

- [x] 7. Atomic Merchant environment switch — เพิ่ม environment-change request ที่ require credential ของทุก connection, Omise callback acknowledgement และ approval ID เดียว, ใช้ Merchant exclusive lock กับ authorization lease, activate/rollback/reject ทุก PSP ภายใน transaction เดียวและ block Merchant ที่มี unresolved legacy snapshot; done เมื่อไม่มี mixed sandbox/live state และทุก failure คง environment/credential ชุดเดิมครบ
     Satisfies: REQ-2.11-REQ-2.16, REQ-8.2-REQ-8.9, REQ-9.3-REQ-9.11, REQ-11.2
     Depends on: 1, 2, 5, 6
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`
     Evidence:
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet build pol-core.slnx --no-restore -warnaserror --no-incremental` -> ผ่าน, 53 projects, 0 errors / 0 warnings (dll เขียนจริง mtime 07:01)
       - test: `NUGET_HTTP_CACHE_PATH=/Users/king_developer/.cache/nuget-http dotnet test pol-core.slnx --no-build --filter 'Category!=Integration'` -> 2,099 passed / 0 failed / 0 skipped ใน 13 projects (Hosts.Tests 724, Architecture.Tests 323, Payments.Tests 273, Admins.Tests 201, Merchants.Tests 183)
       - test: request path ที่ store boundary `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~MerchantEnvironmentControlPlaneTests'` -> 8 passed (AC-7.1 stage-all/202 + Omise ack, environment_credentials_incomplete, unknown-env 400, webhook_not_ready, approval_pending x2 ทิศกลับ AC-7.6, legacy_snapshot_blocked AC-7.5, connection แปลกปลอม 400)
       - test: activation/rollback ที่ executor `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~AdminPaymentsApprovalExecutorTests'` -> 11 passed (AC-7.2 activate ทุก connection + flip merchant, **rollback ทั้งชุดเมื่อ inject vault failure กลาง activation** พิสูจน์บน fresh context + ApprovalExecutionRecords ว่าง, AC-7.3 reject discard ทุก candidate, AC-7.4 candidate หมดอายุ -> credential_candidate_expired, executor completeness revalidation -> environment_credentials_incomplete เมื่อมี connection เกิดใหม่หลังคำขอ)
       - test: mutation-check 5 ด่าน -> ปิด completeness guard (`staged.Count != connections.Count`->`false`), omise-ack guard (`&& false`), legacy-v0 predicate (`== 0`->`== 99`) ใน store แดงทีละด่าน; ปิด candidate-expiry (`candidateExpired = true`->`false`) และ completeness revalidation (`PendingSecretVersionId is null`->`&& false`) ใน executor แดงทีละด่าน -> ทั้ง 5 แดงแล้วเขียวหลัง restore
       - test: pins `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter 'FullyQualifiedName~PermissionGateSitesTests|FullyQualifiedName~AdminTask4ContractTests'` + `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter 'FullyQualifiedName~TransactionInventoryTests|FullyQualifiedName~MerchantRuntimeAuthorizationLeaseTests' --filter 'Category!=Integration'` -> ผ่าน (162 gate sites, OpenAPI RequestMerchantEnvironmentChange If-Match+Idempotency, AdminPaymentsControlStore 15 + executor 3 ExecuteInTransactionAsync, lease VerifyAsync 15)
       - viewports: n/a — logic-only (pol-admin UI อยู่ task 10 นอก scope run นี้)
       - deviations: ไม่มี migration/ตารางใหม่ — `PendingPaymentEnvironment`/`PendingPaymentEnvironmentApprovalId` + domain `StagePaymentEnvironment`/`ActivatePendingPaymentEnvironment`/`RejectPendingPaymentEnvironment` มีครบจาก migration `20260906123608_MerchantPaymentEnvironment` ของ task 2 จึงไม่ต้อง `check-migration-script.sh` และไม่ต้อง GRANT; AC-7.4 (candidate หมดอายุ) และ AC-7.2 completeness revalidation surface เป็น execution-failed outcome ของ approval executor (async ผ่าน governance outbox) ไม่ใช่ 409 sync — ตรง design error map "execution failed"; ด่าน deterministic-precondition (expired/incomplete/stale) commit ผล failed แทน rollback เพื่อไม่ให้ dispatcher retry event เดิมไม่จบ ส่วน exception กลาง activation rollback ทั้ง transaction (AC-7.2 critical #2); AC-7.6 (credential-change เดี่ยวถูกปฏิเสธเมื่อ env change pending) guard อยู่ที่ `RequestCredentialChangeAsync` ของ task 6 แล้ว task 7 เพียงตั้ง `Merchant.PendingPaymentEnvironmentApprovalId`; executor ไม่ re-verify runtime lease (governance decision layer ตรวจ scope+permission+maker-checker ที่ `GovernanceStore.DecideAsync` — precedent task 6 AC-6.7, ไม่มี AC บังคับเปลี่ยน executor signature); Omise callback ack (REQ-11.2) บังคับเฉพาะ target=live ที่มี Omise connection; legacy-v0 predicate = มี row `PaymentSessions.RoutingSnapshotVersion == 0` ของ merchant นั้นทุก status (เป้า remediation ของ task 9)

- [ ] 8. Pinned-secret webhook verification และ guaranteed rematch — แยก bounded reference extraction จาก verification, ให้ 2C2P resolve deterministic Session ก่อนตรวจ JWT, จำกัด pending rematch ให้ Omise fetch-confirm-only, เพิ่ม `InboundWebhookMatchRequested` กับ `PspChargeBound` outbox สองทางและอ่าน retired secret ตาม Session snapshot; done เมื่อ webhook-before-bind ถูกจับคู่ภายหลัง, signed webhook ข้าม verify ไม่ได้และไม่มี raw payload ถูกเก็บหรือคืน
     Satisfies: REQ-2.8-REQ-2.10, REQ-9.6-REQ-9.11, REQ-11.3-REQ-11.8
     Depends on: 1, 2, 4, 6
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

- [ ] 9. Legacy data remediation และ compatibility cutover — ทำ expand/backfill/contract migrations, ซ่อม vault expiry, bootstrap Merchant environment จาก global config, ใช้ snapshot version 0/1, upgrade legacy no-charge Session, พิสูจน์ historical secret ด้วย read-only fetch หรือ block remediation, เก็บ legacy `Psp` compatibility telemetry และเตรียม rollback build; done เมื่อ migration ไม่ blind-backfill, unresolved rows ปิด environment/credential activation และ integration DB ผ่านทั้ง forward/rollback checks
     Satisfies: REQ-2, REQ-3.1-REQ-3.10, REQ-5.11-REQ-5.12, REQ-6.8-REQ-6.18, REQ-8.4-REQ-8.6, REQ-9
     Depends on: 1, 2, 3, 4, 5, 6, 7, 8
     Verify: `source .env.integration && dotnet test pol-core.slnx --filter "Category=Integration"`

- [ ] 10. Admin Console merchant payment settings และ acceptance assembly — ใน `pol-admin` เพิ่ม merchant-first settings page, readiness rail, environment action, provider cards, backend-driven method/routing matrix, advanced-rule read-only state, credential/candidate dialogs, permission/error/ETag handling และ responsive keyboard flow โดย reuse design tokens/components เดิม; ปิดงานด้วย OpenAPI/consumer compatibility, browser verify, full backend/frontend gates และ trace evidenceครบ
     Satisfies: REQ-1, REQ-3, REQ-4.5-REQ-4.13, REQ-5, REQ-6, REQ-7, REQ-8.2-REQ-8.9, REQ-10, REQ-11
     Depends on: 1, 2, 3, 4, 5, 6, 7, 8, 9
     Verify: `scripts/spec-trace.sh merchant-psp-settings`; ใน `pol-admin` รัน `npm run typecheck`, `npm run lint`, `npm test`, `npm run build` และ browser verify ที่ 375/768/1440

## Suggested execution batches

- Backend tasks 1–9 coupled สูง ใช้ `/spec-implement 1-9` ใน session เดียวตาม dependency order
- Task 10 ใช้ session แยกใน `pol-admin` หลัง backend/OpenAPI contract คงที่ แล้วกลับมาบันทึก cross-repository evidence ที่ task นี้
- ไม่มี `Batch:` group เพราะทุก task เป็น domain slice ขนาดใหญ่ ไม่ใช่งาน mechanical ขนาดเล็ก
