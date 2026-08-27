# Implementation Tasks: Merchant Frontend Real API Backend Contract

> Status: unknown

แต่ละ task เป็น backend vertical slice. หมายเลข REQ อ้าง source spine เดียวกับ `pol-merchant`.

- [x] 1. Commerce contract and payment capability — คืน confirmed cart state, บังคับ authoritative
  checkout, validate PSP/proxy config ตอน startup, เพิ่ม paged order/payment APIs และ OpenAPI tests.
     Satisfies: REQ-2, REQ-5, REQ-6, REQ-7, REQ-8, REQ-12, REQ-13.

- [x] 2. Merchant identity, invitation, lifecycle, and RBAC — ปิด OIDC/session/registration contract,
  reuse tenant-bound invitation aggregate, enforce lifecycle/last-manager guards, audit และ permission parity.
     Satisfies: REQ-3, REQ-4, REQ-9, REQ-10, REQ-12, REQ-13. Depends on: 1.
สอง task แชร์ host composition, OpenAPI และ permission inventory. รันใน session เดียวตาม dependency order.
