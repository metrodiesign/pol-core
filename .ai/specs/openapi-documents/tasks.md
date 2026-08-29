# Tasks: OpenAPI Documents by Audience

> Status: unknown

- [x] 1. เพิ่ม audience classifier, register `v1`/`merchant`/`admin`/`integration`, จำกัด security scheme
  และสร้าง active `x-tagGroups`; ตั้ง Scalar selector เป็นสาม named documentsโดย `merchant` เป็น default.
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5, REQ-2.1, REQ-2.2, REQ-2.3,
     REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7, REQ-2.8, REQ-2.9, REQ-3.1, REQ-3.2, REQ-3.3,
     REQ-3.4, REQ-3.5, REQ-3.6.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 2. เติม summary/description ของ operation ให้ครบ และแก้ session security text ให้ตรง provider-scoped OIDC ปัจจุบัน โดยไม่เปลี่ยน API contract หรือ runtime behavior.
     Satisfies: REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
