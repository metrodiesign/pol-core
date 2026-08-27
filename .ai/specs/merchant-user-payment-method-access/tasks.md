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
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 2. **Provider and account capability control plane with authorization locks**
     เพิ่ม canonical Method normalization, transaction-owned global/Merchant lock protocol, unrestricted Admin catalog/Provider/Provider Method/Provider Option GET/PUT, scoped Account Method/Option mutation, platform/tenant idempotency, ETag/audit, adapter `SupportedMethods` ceiling และ legacy PSP mutation façade/projection โดยไม่คืน credential — done เมื่อ exact Provider/account chain แก้ได้เฉพาะ scope ที่อนุญาต, drift/parent mismatch fail closed และ concurrent mutation serialize ตาม lock order
     Satisfies: REQ-2.14-REQ-2.15, REQ-3.1-REQ-3.5, REQ-3.9-REQ-3.24. Depends on: 1. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~PaymentProviderCapability|FullyQualifiedName~PaymentAccountCapability|FullyQualifiedName~PaymentAuthorizationLock"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 3. **Canonical effective resolver, options intersection and self-service reads**
     Implement `IEffectivePaymentCapabilityResolver` สำหรับ Merchant User/Platform Admin, any-Provider และ selected-Provider decisions, exact Provider/Account option intersection, adapter ceiling, fresh state reads และ Merchant User self Method/Option endpoints จาก server identity เท่านั้น — done เมื่อทุก missing/disabled layer deny, options ไม่ union/fallback และ response/error ไม่เปิด policy topologyหรือ tenant อื่น
     Satisfies: REQ-5 (all criteria), REQ-6.4-REQ-6.5, REQ-6.8-REQ-6.12, REQ-6.18-REQ-6.21, REQ-6.26, REQ-9 (all criteria). Depends on: 1, 2. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~EffectivePaymentCapabilityResolver|FullyQualifiedName~EffectivePaymentOptions|FullyQualifiedName~MerchantPaymentSelfRead"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 4. **Merchant/User policy administration and five-query contract**
     เพิ่ม Merchant/User policy enable-disable พร้อม exact parent recheckใน transaction, deny-default child semantics, sanctioned cross-tenant Admin store, five required queries, applicant separation, scoped 404, permissions, CSRF, idempotency, ETag/audit และห้าม Merchant User mutation — done เมื่อ qualifying account เป็น prerequisite, cross-Merchant/duplicate writes ถูก DB ปฏิเสธ และ API contracts ทั้งห้าผ่าน resolver เดียว
     Satisfies: REQ-3.6-REQ-3.8, REQ-4 (all criteria), REQ-6.1-REQ-6.3, REQ-6.6-REQ-6.7, REQ-6.11, REQ-6.13-REQ-6.17, REQ-6.19, REQ-6.22-REQ-6.25, REQ-6.27-REQ-6.28. Depends on: 1, 2, 3. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~PaymentPolicyAdministration|FullyQualifiedName~PaymentCapabilityQueries|FullyQualifiedName~PaymentPolicyTenantIsolation"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. **Order and Payment Session authorization boundary**
     Persist server-derived initiating audience/Userและ immutable canonical Order Method, retire bypassing `CreateOrderCommand` path, make `AttachPaymentAttempt` method-free and `MarkPaid` compare-only, then place Order creation/Session creation plus User/Merchant status writers under shared/exclusive authorization locks and current resolver — done เมื่อ generic RBAC bypassไม่ได้, Admin skips only User layer, Session Method mismatch failsก่อน insert และ revoke race linearizesกับ both writes
     Satisfies: REQ-7.1-REQ-7.2, REQ-7.4-REQ-7.5, REQ-7.7-REQ-7.8, REQ-7.10-REQ-7.15. Depends on: 1, 3, 4. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~OrderPaymentAuthorization|FullyQualifiedName~CreateSessionAuthorization|FullyQualifiedName~OrderPaymentMethodInvariant"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 6. **First-charge claim and anonymous payment enforcement**
     Re-resolve trusted Order subjectก่อน first redirect claim, commit claimก่อน PSP call, derive anonymous Merchant/Method/audience/Userจาก server-side Order, preserve token/expiry/rate limit และ continue existing-claim/webhook/status settlementโดยไม่ re-authorizeหรือสร้าง replacement charge — done เมื่อ revokeก่อน claim blocks PSP, post-claim revokeยัง reconcile idempotently และ Admin-originated Orderยังผ่าน parent capability
     Satisfies: REQ-7.3, REQ-7.6, REQ-7.9, REQ-7.16-REQ-7.19, REQ-8 (all criteria). Depends on: 5. Verify: `dotnet test pol-core.slnx --filter "FullyQualifiedName~FirstChargeAuthorization|FullyQualifiedName~AnonymousPaymentAuthorization|FullyQualifiedName~PostClaimReconciliation"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 7. **Deterministic compatibility backfill, verified cutover and rollback**
     Implement LegacyRead/NormalizedRead/FailClosed rollout, deterministic Account/Merchant CSV backfill, Active User grants, durable Admin Order marker, unique Active/Suspended creator mapping, remediation conflicts, compatibility projections, old-instance drain check, exclusive global cutover, final Account→Merchant→User→Order delta reconciliation, atomic verification/mode flip และ normalized-aware rollback — done เมื่อ failure rolls transaction back, unresolved/drift conflicts block cutover และไม่มี production migrationถูกเรียกจาก implementation
     Satisfies: REQ-2.6-REQ-2.7, REQ-10 (all criteria). Depends on: 1-6. Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "FullyQualifiedName~PaymentCapabilityMigration|FullyQualifiedName~PaymentAuthorizationCutover|FullyQualifiedName~PaymentCapabilityRollback"`.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 8. **Security, compatibility and acceptance assembly**
     ปิด specด้วย SQL constraint/tenant guard tests, adapter drift release check, five-query tests, deterministic authorization/cutover race tests, Order writer/lock-order/context-ownership architecture guards, User A/User B + KBANK/SCB/KTC/BAY acceptance fixtures, existing payment regressions และ full implementation gate — done เมื่อ evidenceทุก REQ ถูก trace, SQL integrationรายงานตามจริงและทุก runnable gateเขียว
     Satisfies: REQ-11 (all criteria). Depends on: 1-7. Verify: run Final implementation gate below; if SQL Server unavailable, record integration tests as not run and do not claim green.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
