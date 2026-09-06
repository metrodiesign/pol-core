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

- [ ] 3. Verified provider capabilities และ policy สองระดับ — ให้ `IPspAdapter.SupportedMethods` หมายถึง method ที่มี sandbox evidence, ปิด Omise ทุก method จน dependency spec ผ่าน, คง 2C2P สาม method, แยก account method จาก Merchant method และคืน effective denial reason จาก backend; done เมื่อ implementation ที่ยังไม่พิสูจน์เปิดไม่ได้, CSV/JSON ไม่เป็น authorization source และ connection ที่ไม่มี method ยังจัดการ credential ได้
     Satisfies: REQ-5, REQ-6.6-REQ-6.7, REQ-6.14-REQ-6.16
     Depends on: 2
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

- [ ] 4. Server-side routing authority และ immutable Payment Session snapshot — ย้าย PSP selection เข้า `CreateSessionHandler` ใต้ Merchant shared lock, คืน `PspRouteSelection`, ตรึง connection/secret/environment, ใช้ primary/fallback เฉพาะก่อน PSP call, recheck emergency kill ก่อน first claim และเพิ่ม compatibility request ที่รับแต่ไม่ใช้ legacy `Psp`; done เมื่อทุก audience ใช้ routing เดียว, timeout ไม่ failover และ settings change ไม่เปลี่ยน attempt เดิม
     Satisfies: REQ-2.7-REQ-2.10, REQ-3.5, REQ-5.6-REQ-5.7, REQ-5.15, REQ-6.8-REQ-6.18
     Depends on: 1, 2, 3
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

- [ ] 5. Simple routing API ที่ป้องกัน advanced-rule overwrite — เพิ่ม GET/PUT merchant simple-routing contract, สร้างหรือแทนเฉพาะ draft ที่ไม่มี `any`, amount หรือ Originator predicates, validate primary/fallback/coverage/environment ที่ server และใช้ routing activation maker-checker เดิม; done เมื่อ stale/malicious client เขียนทับ advanced rules ไม่ได้และทุก Merchant method ที่เปิดมี primary ก่อน activation
     Satisfies: REQ-6.1-REQ-6.7, REQ-6.12-REQ-6.21, REQ-9.3-REQ-9.9
     Depends on: 1, 2, 3, 4
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

- [ ] 6. Credential rotation และ optional candidate test — stage candidate พร้อม target environment, เพิ่ม read-only candidate-test endpoint ที่ compare pending/version หลัง probe, ให้ maker-checker activate/reject โดยไม่บังคับ test, reset active health หลัง activation และรักษา retired version ที่ Session อ้าง; done เมื่อ maker อนุมัติเองไม่ได้, pending ซ้ำได้ 409, reject cleanup ครบ และ candidate test ไม่เปลี่ยน active state
     Satisfies: REQ-2.8-REQ-2.10, REQ-7.8-REQ-7.11, REQ-8, REQ-9.3-REQ-9.11
     Depends on: 1, 2, 4
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

- [ ] 7. Atomic Merchant environment switch — เพิ่ม environment-change request ที่ require credential ของทุก connection, Omise callback acknowledgement และ approval ID เดียว, ใช้ Merchant exclusive lock กับ authorization lease, activate/rollback/reject ทุก PSP ภายใน transaction เดียวและ block Merchant ที่มี unresolved legacy snapshot; done เมื่อไม่มี mixed sandbox/live state และทุก failure คง environment/credential ชุดเดิมครบ
     Satisfies: REQ-2.11-REQ-2.16, REQ-8.2-REQ-8.9, REQ-9.3-REQ-9.11, REQ-11.2
     Depends on: 1, 2, 5, 6
     Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"`

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
