# Implementation Tasks: Merchant User Payment Method Access

> Status: approved 2026-08-17

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.
> Feature นี้ coupled สูง: schema, resolver, payment lifecycle และ cutover ใช้ primitives ร่วมกัน
> จึงควร implement ตามลำดับใน session เดียว

- [x] 1. **Capability persistence foundation and additive expand migration**
     Reuse `merch.Users`, Merchant, PSP connection และ vault model; เพิ่ม global `cfg` catalog, tenant `txn` capability policies, authorization state/conflict relations, Provider-adapter identity, Order initiating columns, exact Guid PK/FK/unique/check/composite constraints, runtime-context ownership/guards และ deterministic canonical seed โดยไม่สร้าง production bank assignments — done เมื่อ migration additive ใช้กับ current schema ได้และ SQL Server tests ปฏิเสธ identity/tenant/parent-chain violations ครบ
     Satisfies: REQ-1 (all criteria), REQ-2.1-REQ-2.5, REQ-2.8-REQ-2.13. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~PaymentCapabilitySchema|FullyQualifiedName~MerchantUserIdentityBoundary|FullyQualifiedName~PaymentCapabilityOwnership"`.
     Evidence:

     - **Implementation**: เพิ่ม 10 normalized capability entities, 2 migration-control entities, runtime owner/filter/write-guard mappings และ nullable Order/account expand fields โดย reuse User, Merchant, PSP connection และ vault เดิม
     - **Migration**: `20260817170326_MerchantUserPaymentMethodAccessExpand` ผ่าน fresh SQL Server Up/Down; seed methods 3, providers 2, provider methods 4 และ canonical bank options 4 โดยไม่มี Provider/Account option assignment
     - **Constraints**: SQL Server ปฏิเสธ Active User ที่ไม่มี Merchant, cross-Merchant User policy, account/provider mismatch, option-chain mismatch และ Provider/adapter mismatch
     - **Verify**: exact task command ผ่าน Architecture 7 tests และ SQL Server Integration 1 test; `ModelConsistencyTests` ผ่าน 1 test และ fresh migration round-trip ผ่าน 1 test
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 2. **Provider and account capability control plane with authorization locks**
     เพิ่ม canonical Method normalization, transaction-owned global/Merchant lock protocol, unrestricted Admin catalog/Provider/Provider Method/Provider Option GET/PUT, scoped Account Method/Option mutation, platform/tenant idempotency, ETag/audit, adapter `SupportedMethods` ceiling และ legacy PSP mutation façade/projection โดยไม่คืน credential — done เมื่อ exact Provider/account chain แก้ได้เฉพาะ scope ที่อนุญาต, drift/parent mismatch fail closed และ concurrent mutation serialize ตาม lock order
     Satisfies: REQ-2.14-REQ-2.15, REQ-3.1-REQ-3.5, REQ-3.9-REQ-3.24. Depends on: 1. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~PaymentProviderCapability|FullyQualifiedName~PaymentAccountCapability|FullyQualifiedName~PaymentAuthorizationLock"`.
     Evidence:

     - **Control plane**: เพิ่ม paired GET/PUT สำหรับ Method, Provider, Provider Method, Provider Option, Account Method และ Account Option พร้อม Admin scope, CSRF, permission, ETag, idempotency และ audit actor/time
     - **Invariant**: ทุก enable ตรวจ exact parent chain กับ adapter `SupportedMethods`; missing normalized row ใช้ deny-default `v0`; legacy PSP create/update funnel เข้า account rowsและ deterministic CSV projection
     - **Serialization**: global mutationถือ exclusive `payment-authz:global`; tenant mutationถือ shared globalแล้ว exclusive Merchant lockแบบ transaction-owned
     - **Migration**: `20260817172338_MerchantPaymentCapabilityControlPlane` เพิ่ม nullable audit columns 4 ช่องพร้อม Down rollback
     - **Verify**: exact task commandผ่าน Architecture 3, Hosts 2 และ SQL Server Integration 2 tests; build `-warnaserror` ผ่าน
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 3. **Canonical effective resolver, options intersection and self-service reads**
     Implement `IEffectivePaymentCapabilityResolver` สำหรับ Merchant User/Platform Admin, any-Provider และ selected-Provider decisions, exact Provider/Account option intersection, adapter ceiling, fresh state reads และ Merchant User self Method/Option endpoints จาก server identity เท่านั้น — done เมื่อทุก missing/disabled layer deny, options ไม่ union/fallback และ response/error ไม่เปิด policy topologyหรือ tenant อื่น
     Satisfies: REQ-5 (all criteria), REQ-6.4-REQ-6.5, REQ-6.8-REQ-6.12, REQ-6.18-REQ-6.21, REQ-6.26, REQ-9 (all criteria). Depends on: 1, 2. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~EffectivePaymentCapabilityResolver|FullyQualifiedName~EffectivePaymentOptions|FullyQualifiedName~MerchantPaymentSelfRead"`.
     Evidence:

     - **Resolver**: เพิ่ม canonical `IEffectivePaymentCapabilityResolver` สำหรับ Merchant User/Platform Admin, any-Provider และ selected-Provider โดยถือ shared authorization locks และอ่าน current normalized stateทุก request
     - **Intersection**: ตรวจ User, Merchant, Method, Merchant/User policy, enabled Account/Account Method, active Provider Method และ adapter ceiling; options intersect exact Provider/Account rowsโดยไม่ union/fallback
     - **Self read**: เพิ่ม GET methods/options ที่ใช้ `IActorContext` เท่านั้น, gate `payment.view`, canonical lowercase output และไม่มี target Merchant/User inputหรือ mutation route
     - **Verify**: exact task commandผ่าน SQL Server Integration 3 tests และ Hosts 1 test; build `-warnaserror` ผ่าน
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 4. **Merchant/User policy administration and five-query contract**
     เพิ่ม Merchant/User policy enable-disable พร้อม exact parent recheckใน transaction, deny-default child semantics, sanctioned cross-tenant Admin store, five required queries, applicant separation, scoped 404, permissions, CSRF, idempotency, ETag/audit และห้าม Merchant User mutation — done เมื่อ qualifying account เป็น prerequisite, cross-Merchant/duplicate writes ถูก DB ปฏิเสธ และ API contracts ทั้งห้าผ่าน resolver เดียว
     Satisfies: REQ-3.6-REQ-3.8, REQ-4 (all criteria), REQ-6.1-REQ-6.3, REQ-6.6-REQ-6.7, REQ-6.11, REQ-6.13-REQ-6.17, REQ-6.19, REQ-6.22-REQ-6.25, REQ-6.27-REQ-6.28. Depends on: 1, 2, 3. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~PaymentPolicyAdministration|FullyQualifiedName~PaymentCapabilityQueries|FullyQualifiedName~PaymentPolicyTenantIsolation"`.
     Evidence:

     - **Policy writes**: Merchant enable rechecks active account/provider/adapter chainใต้ exclusive authorization lock; User enable rechecks bound Active/Suspended actorกับ enabled Merchant policyใน transactionเดียวกัน
     - **Deny-default**: missing/disabled rows deny; disabling Merchantเก็บ enabled User rowไว้แต่ resolverคืน ineffective; actor/time, Version และ tenant-scoped operation replayใช้ modelเดิม
     - **Five queries**: Admin routesครบ Users, Merchant methods, explicit User methods, resolution และ selected-Provider options; paired policy GET/PUTมี ETag, CSRF, idempotency และ existing permissions
     - **Isolation**: Admin Merchant Users queryคืนเฉพาะ bound Active/Suspended; out-of-scope/mismatched Userคืน 404 และ SQL composite/unique constraintsปฏิเสธ cross-Merchant/duplicate writes
     - **Verify**: exact task commandผ่าน Integration 2 tests และ Hosts 1 test
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. **Order and Payment Session authorization boundary**
     Persist server-derived initiating audience/Userและ immutable canonical Order Method, retire bypassing `CreateOrderCommand` path, make `AttachPaymentAttempt` method-free and `MarkPaid` compare-only, then place Order creation/Session creation plus User/Merchant status writers under shared/exclusive authorization locks and current resolver — done เมื่อ generic RBAC bypassไม่ได้, Admin skips only User layer, Session Method mismatch failsก่อน insert และ revoke race linearizesกับ both writes
     Satisfies: REQ-7.1-REQ-7.2, REQ-7.4-REQ-7.5, REQ-7.7-REQ-7.8, REQ-7.10-REQ-7.15. Depends on: 1, 3, 4. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~OrderPaymentAuthorization|FullyQualifiedName~CreateSessionAuthorization|FullyQualifiedName~OrderPaymentMethodInvariant"`.
     Evidence:

     - **Order boundary**: checkout derives `MerchantUser`/`PlatformAdmin` subjectจาก server actor, persists immutable canonical Methodและ initiating context, holds shared authorization lockและ resolves capabilityก่อน Order/cart/outbox write
     - **Session boundary**: create/resume `Created` re-reads trusted Orderใต้ lock, rejects Method/Merchant mismatchก่อน insert, resolves current capability และ uses Order Method only; `AttachPaymentAttempt` no longer accepts Method
     - **Settlement invariant**: `MarkPaid` compares PSP Methodกับ immutable Order Methodโดยไม่ mutate; legacy bypassing `CreateOrderCommand`/handlerถูกลบ
     - **Status serialization**: Merchant/User activation, suspension และ reactivation acquire exclusive authorization lockร่วมกับ resolver writers
     - **Verify**: exact task commandผ่าน Payments 1, Orders 1 และ Hosts 1 test
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 6. **First-charge claim and anonymous payment enforcement**
     Re-resolve trusted Order subjectก่อน first redirect claim, commit claimก่อน PSP call, derive anonymous Merchant/Method/audience/Userจาก server-side Order, preserve token/expiry/rate limit และ continue existing-claim/webhook/status settlementโดยไม่ re-authorizeหรือสร้าง replacement charge — done เมื่อ revokeก่อน claim blocks PSP, post-claim revokeยัง reconcile idempotently และ Admin-originated Orderยังผ่าน parent capability
     Satisfies: REQ-7.3, REQ-7.6, REQ-7.9, REQ-7.16-REQ-7.19, REQ-8 (all criteria). Depends on: 5. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~FirstChargeAuthorization|FullyQualifiedName~AnonymousPaymentAuthorization|FullyQualifiedName~PostClaimReconciliation"`.
     Evidence:

     - **First claim**: `StartRedirectHandler` acquires shared authorization lock, locks/re-reads authoritative Order, resolves selected Provider และ persists redirect claimใน transactionก่อน adapter call
     - **Revocation**: User inactive/policy denialคืน 403ก่อน claim, vault read หรือ PSP call; parent capability denialคืน 409
     - **Anonymous**: token routeยัง anonymous/rate-limited, accepts no payment context parameters และ uses server-derived Order Merchant/Method/initiating subject; client JSON overridesไม่มีผล
     - **Post-claim**: Redirected claim with/without URL skips resolverและ settles same Session idempotency key; webhook/status paths unchangedและไม่ re-authorize
     - **Verify**: exact task commandผ่าน Payments 3 และ Hosts 1 test; full anonymous endpoint suiteผ่าน 19 tests
     - **Deviation**: ไม่มี

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 7. **Deterministic compatibility backfill, verified cutover and rollback**
     Implement LegacyRead/NormalizedRead/FailClosed rollout, deterministic Account/Merchant CSV backfill, Active User grants, durable Admin Order marker, unique Active/Suspended creator mapping, remediation conflicts, compatibility projections, old-instance drain check, exclusive global cutover, final Account→Merchant→User→Order delta reconciliation, atomic verification/mode flip และ normalized-aware rollback — done เมื่อ failure rolls transaction back, unresolved/drift conflicts block cutover และไม่มี production migrationถูกเรียกจาก implementation
     Satisfies: REQ-2.6-REQ-2.7, REQ-10 (all criteria). Depends on: 1-6. Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~PaymentCapabilityMigration|FullyQualifiedName~PaymentAuthorizationCutover|FullyQualifiedName~PaymentCapabilityRollback"`.
     Evidence:

     - **Rollout state**: resolver re-reads database `LegacyRead`/`NormalizedRead`/`FailClosed` each request; options stay closed outside normalized mode และ post-cutover backfillถูกปฏิเสธเพื่อไม่ auto-grant User ใหม่
     - **Backfill/cutover**: deterministic Account→Merchant→Active User→Order reconciliation, adapter intersection, durable remediation conflicts, old-instance drain gate, database UTC cutoff, exclusive global lock, verification, non-null CHECK enforcement และ mode flipอยู่ transactionเดียว
     - **Compatibility**: normalized Account/Merchant writes project canonical CSV; legacy PSP/Merchant façades sync normalized rows; provisioningถือ authorization lockและสร้าง Provider binding, Account Methods กับ Merchant policiesครบ
     - **Rollback**: pre-cutoverคง Legacy; post-cutoverใช้ Normalized หรือ FailClosed เท่านั้น; failed cutover rollback atomicและ conflictยัง durable
     - **Verify**: exact task commandผ่าน SQL Server Integration 5 tests; provisioning compatibilityผ่าน Merchants 15 และ Architecture 10 tests; build `-warnaserror` ผ่าน
     - **Deviation**: ไม่รัน production migration

      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 8. **Security, compatibility and acceptance assembly**
     ปิด specด้วย SQL constraint/tenant guard tests, adapter drift release check, five-query tests, deterministic authorization/cutover race tests, Order writer/lock-order/context-ownership architecture guards, User A/User B + KBANK/SCB/KTC/BAY acceptance fixtures, existing payment regressions และ full implementation gate — done เมื่อ evidenceทุก REQ ถูก trace, SQL integrationรายงานตามจริงและทุก runnable gateเขียว
     Satisfies: REQ-11 (all criteria). Depends on: 1-7. Verify: run Final implementation gate below; if SQL Server unavailable, record integration tests as not run and do not claim green.

     Evidence:

     - **Acceptance**: SQL fixtureพิสูจน์ User A ได้ `card,promptpay`, User B ได้ `installment`; option intersectionคืนเฉพาะ `KBANK,SCB` และกรอง disabled `KTC` กับ missing `BAY`
     - **Security/architecture**: relational guardsครอบคลุม identity uniqueness, duplicate policy, composite tenant FK และ provider/account chain; architecture tests pin resolver/writer transaction, lock order, context ownership, bypass ports และ request-path read guard
     - **Compatibility/API**: CORS แยก admin/self routes, permission inventory 178 sites, OpenAPI summary/descriptionครบ และ legacy host fixturesใช้ authorization seamsใหม่โดยไม่ลด production enforcement
     - **Final gate**: restoreผ่าน; build `-warnaserror` ผ่าน 0 warnings/0 errors; non-integration 1,782 testsผ่าน; SQL Server Integration 163 testsผ่าน; rename, full-tree secret scan และ spec trace 225 criteriaผ่าน
     - **Deviation**: internal audience enum memberใช้ `User = 1` แทน bare retired identifier `MerchantUser`; wire/DB valueไม่เปลี่ยน และไม่รัน production migration

## Final implementation gate

```bash
dotnet restore pol-core.slnx
dotnet build pol-core.slnx --no-restore -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test pol-core.slnx --filter "Category=Integration"
scripts/check-rename-identifiers.sh
.ai/bin/check-secrets.sh --all
scripts/spec-trace.sh merchant-user-payment-method-access
```

ห้าม commit `.only`/`.skip`, เพิ่ม dependency, expose secret, รัน migrationบน production หรืออ้าง SQL integrationว่า greenเมื่อไม่ได้รัน

## Suggested execution batches

> DEFAULT: run all tasks in one session because capability schema, lock protocol, resolver,
> payment lifecycle and cutover share state heavily:
> `scripts/pane-loop.sh merchant-user-payment-method-access all-in-one`
> หรือ `/spec-implement all` ตามลำดับ 1→2→3→4→5→6→7→8

ไม่มี `Batch:` tag งานทุกก้อนใหญ่และมี dependencyจริง การแยก sessionเหมาะเฉพาะเพื่อ isolate CORE payment logicหลัง task 4 ไม่ใช่เพื่อรันขนาน
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
