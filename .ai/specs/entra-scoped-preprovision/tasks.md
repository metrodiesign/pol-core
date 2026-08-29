# Implementation Tasks: Entra Scoped Pre-provision

> Status: approved 2026-08-19

แต่ละ task เป็น slice ที่ตรวจได้เองและต้อง implement พร้อม tests ในรอบเดียว. Feature นี้แชร์
transaction, persistence และ auth flow จึงควรรันทุก task ใน session เดียวตามลำดับ dependency

- [x] 1. **ตรึง Workforce tenant และทำ migration ปลอดภัย** — validate public-cloud Authority, persist immutable singleton ก่อน listen และ canonicalize existing subjects ด้วย exact SQL guards
     Satisfies: REQ-1.16-1.20, REQ-6.5, REQ-6.14.

- [x] 2. **ผูก Microsoft identity แบบ atomic พร้อม tamper-evident audit** — เพิ่ม command/handler, one-time binding, Active Super lease และ platform hash chain โดยคง authorization state
     Satisfies: REQ-1.2-1.4, REQ-1.12, REQ-1.14, REQ-1.22-1.23, REQ-2.1-2.12, REQ-2.16, REQ-5.1-5.6, REQ-5.10-5.12, REQ-6.7, REQ-6.15.

- [x] 3. **ทำ retry, no-op และ concurrent binding ให้ deterministic** — reuse operation records, enforce ETag/idempotency ordering และ map races โดยไม่เกิด partial write
     Satisfies: REQ-1.8-1.9, REQ-1.15, REQ-2.13-2.18, REQ-3.1-3.9, REQ-6.10-6.11, REQ-6.15.

- [x] 4. **เปิด Super-only pre-provision HTTP contract** — เพิ่ม PUT endpoint, security/validation gates, minimal response/ETag, OpenAPI และ RFC 9457 stable codes
     Satisfies: REQ-1.1, REQ-1.5-1.15, REQ-1.17, REQ-1.21, REQ-1.24, REQ-2.3, REQ-2.10-2.14, REQ-2.18-2.19, REQ-3.9, REQ-5.8-5.9, REQ-6.8-6.9, REQ-6.16.

- [x] 5. **บังคับ Entra `tid`/`oid` และลด identity leakage หลัง login** — ใช้ canonical tenant-local claims, ตัด external subject จาก session/audits และรักษา login เดิม
     Satisfies: REQ-1.17, REQ-4.1-4.12, REQ-5.7-5.8, REQ-5.13, REQ-6.1-6.6, REQ-6.12-6.13.

- [x] 6. **ปิด SQL, security และ Lab acceptance gate** — พิสูจน์ migration, races, rollback, audit chain, replay retention, full contract และ employee login/negative control
     Satisfies: REQ-1.18-1.20, REQ-1.22-1.24, REQ-2.15-2.17, REQ-3.1-3.9, REQ-5.1, REQ-5.7-5.13, REQ-6.7-6.16.
หรือใช้ `/spec-implement all`. ไม่มี `Batch:` tag เพราะแต่ละ task ใหญ่และมี verification boundary ของตนเอง
