# Implementation Tasks: Admin Console Real API Backend Contract

> Status: unknown

ทำตามลำดับ 1 → 9. แต่ละ task ต้องจบ owner code, migration, targeted tests, OpenAPI และ
Evidence ก่อนเริ่ม task ถัดไป. ห้าม stub success, duplicate route/aggregate, หรือ coordinator ใหม่.

- [x] 1. Admin session and `dual-console` delivery spine — เพิ่ม platform permission keys/seeds,
  deterministic audience selection, paired permission, audience CSRF, OpenAPI OR และ isolated tests.
  Existing Commerce plus `ListMerchantUsers`/`GetMerchantUser` ยัง pinned จน Task 5/6 เพิ่ม Admin
  owner branchพร้อม route adoption; pure-audience routesไม่เปลี่ยน.
     Satisfies: REQ-1.7–1.8, REQ-1.10, REQ-2.1–2.2, REQ-2.7–2.11, REQ-2.13–2.15, REQ-15.10–15.11, REQ-16.3.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 2. Governance and immutable audit foundation — เพิ่ม approval/audit persistence,
  `admin.OperationRecords`, owner-request → decision → owner-execution outbox protocol,
  maker-checker/version checks, append-only hash/redaction, and query endpoints. Governance never
  writes target contexts.
     Satisfies: REQ-12.1–12.7, REQ-12.12–12.14, REQ-15.10–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 3. Admin identity, roles, and organization masters — extend existing Admin/Iam/Office/
  Division/Position/Level handlers with one-page server pagination, detail, ETag, stable conflicts,
  idempotent session revoke, and unchanged operation IDs.
     Satisfies: REQ-1.1–1.2, REQ-7.1–7.11, REQ-9.1–9.6, REQ-9.8, REQ-15.13–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 4. Tenant, Originator, PSP, and Routing control plane — add tenant list/update/status,
  five Originator kinds, PSP configuration/test, staged MerchantRuntime Vault versions, routing drafts,
  governed activation, active-ruleset selection, and `txn.AdminOperationRecords`.
     Satisfies: REQ-10.1–10.12, REQ-14.1–14.8, REQ-15.9–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 5. Merchant users and merchant-owned roles — extend existing `ListMerchantUsers` and
  `GetMerchantUser` through Task 1 policy while preserving IDs/handlers; add Admin invitation/update
  and merchant-owned role APIs. Reuse canonical invitation aggregate/table/outbox and exact consume rules.
     Satisfies: REQ-2.16, REQ-8.1–8.14, REQ-15.10–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 6. Admin policy-to-payment-link and Order lifecycle — add `/products/documents`; extend
  existing Cart/Order/PaymentSession operations through Task 1 policy with explicit merchant/originator,
  audience-aware request schemas, routing selection, durable operation state, hosted link, lifecycle,
  and Order export. Preserve Merchant success bodies and operation IDs.
     Satisfies: REQ-4.1–4.6, REQ-4.8–4.10, REQ-4.14–4.16, REQ-5.1–5.7, REQ-5.9–5.14, REQ-15.9–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 7. Dashboard, Transactions, Reconciliation, and Reports — add query-only dashboard,
  transaction projection/detail/capability, bounded transaction/order/report exports, and operations
  reports. Extend existing `Orders` reconciliation only; create no Transaction aggregate.
     Satisfies: REQ-3.1–3.4, REQ-6.1–6.14, REQ-13.1–13.7, REQ-15.9–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 8. API clients, Webhooks, and Notifications — add API-client lifecycle and one-time reveal,
  inbound event query, SSRF-safe outbound endpoints/delivery/replay, notification rules/logs, and
  ControlPlane-owned `admin.DeliverySecretVersions`. Reuse Task 2 Governance protocol.
     Satisfies: REQ-11.1–11.12, REQ-12.8–12.11, REQ-15.10–15.15.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 9. Backend-wide contract and release verification — no feature code. Prove exact operation
  inventory, persisted behavior, owner boundaries, security, migrations, and cross-repository trace.
     Satisfies: REQ-1.1–1.2, REQ-1.6–1.8, REQ-1.10, REQ-16.3, REQ-16.6, REQ-16.8, REQ-16.13.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
