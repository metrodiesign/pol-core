# Implementation Tasks: Merchant Frontend Real API Backend Contract

> Status: unknown

แต่ละ task เป็น backend vertical slice. หมายเลข REQ อ้าง source spine เดียวกับ `pol-merchant`.

- [x] 1. Commerce contract and payment capability — คืน confirmed cart state, บังคับ authoritative
  checkout, validate PSP/proxy config ตอน startup, เพิ่ม paged order/payment APIs และ OpenAPI tests.
  Satisfies: REQ-2, REQ-5, REQ-6, REQ-7, REQ-8, REQ-12, REQ-13.
  Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"` และ `dotnet build pol-core.slnx --no-restore`.
  Evidence: offline suite 1,604/1,604, build 0 warnings/errors, cart tests 25/25,
  startup/merchant host filter 16/16, spec trace 57/57 และ Mermaid 2/2 ผ่าน.

- [x] 2. Merchant identity, invitation, lifecycle, and RBAC — ปิด OIDC/session/registration contract,
  reuse tenant-bound invitation aggregate, enforce lifecycle/last-manager guards, audit และ permission parity.
  Satisfies: REQ-3, REQ-4, REQ-9, REQ-10, REQ-12, REQ-13. Depends on: 1.
  Verify: targeted Hosts/Merchants/Iam/Architecture tests, offline suite และ SQL integrationเมื่อมี credentials.
  Evidence: offline suiteรวม Merchants 167/167, Hosts 447/447 และ Architecture 233/233 ผ่าน; SQL integrationรอบนี้
  ไม่มี local principal passwords จึงไม่รัน (evidence ก่อน reconciliation 145/145; schemaไม่เปลี่ยนหลังรอบนั้น).

## Suggested Execution Batch

สอง task แชร์ host composition, OpenAPI และ permission inventory. รันใน session เดียวตาม dependency order.
