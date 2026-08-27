# Implementation Tasks: Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)

> Status: approved 2026-08-23

แต่ละ task เป็น slice ที่ implement และตรวจได้ในรอบเดียวพร้อม tests. Feature นี้แชร์ canonicalizer,
Admin identity transaction และ migration cutover จึงต้องทำตาม dependency และคง approval gate ที่ task boundary

## Implementation checklist

- [x] 1. **สร้าง canonical email identity foundation** — เพิ่ม BCL-based `WorkforceEmail`, บังคับ User subject/email/key invariants และ persist nullable unique `WorkforceEmailKey` โดยไม่เพิ่ม dependency
     Satisfies: REQ-2.10-2.17, REQ-2.29-2.31, REQ-3.1-3.5, REQ-4.17, REQ-4.22, REQ-4.24-4.25, REQ-8.8. Verify: Admin domain tests และ real-SQL canonical-key collation/uniqueness tests.

- [x] 2. **Resolve, bind หรือ JIT Admin แบบ atomic** — ใช้ candidate set เดียวภายใต้ identity mutation lock, bind Active unbound owner, reject suspended/divergent owners, create roleless Scoped JIT และ commit identity audit พร้อม mutation
     Satisfies: REQ-3.3-3.13, REQ-4.1-4.25, REQ-5.1-5.12, REQ-7.1-7.6, REQ-7.10-7.14, REQ-7.19-7.21, REQ-9.19-9.23. Depends on: 1. Verify: Admin handler/domain tests และ real-SQL conflict, rollback, bind/JIT concurrency tests.

- [x] 3. **เปลี่ยน Tier 0 OIDC callback เป็น canonical email** — ตรวจ exact tenant และ claim precedence หลัง middleware validation, ignore `roles`/`oid`, ส่ง Microsoft callback เข้า resolver เดียว และสร้าง session เฉพาะ Active resolved Admin
     Satisfies: REQ-1 (all criteria), REQ-2 (all criteria), REQ-4.19-4.21, REQ-7.5-7.9, REQ-7.12-7.18, REQ-8.1-8.4, REQ-9.11-9.18, REQ-9.20, REQ-9.24. Depends on: 1-2. Verify: Host OIDC, callback, session, failure-classification และ provider-regression tests.

- [x] 4. **Migrate oid subjects แบบ fail closed** — เพิ่ม snapshot/state manifest, privileged console migrator, completed-state startup invariant gate, guarded Down และ migrate-container sequencing พร้อม atomic preflight
     Satisfies: REQ-6.1-6.18, REQ-6.20-6.28, REQ-9.25. Depends on: 1-2. Verify: real-SQL valid/no-op/rejection/idempotency/rollback tests และ migrate-service non-zero gate test.

- [x] 5. **Retire oid pre-provision และปรับ operational documentation** — ลบ endpoint/handler/audit wiring เดิม, คง normal `404`, ลบ current App Role prerequisite และ document claim rules, backup, maintenance, rollback cutoff และ email rename/reuse risk
     Satisfies: REQ-6.19, REQ-8.1-8.7, REQ-9.1-9.10, REQ-9.28. Depends on: 3-4. Verify: retired-route no-mutation test, production-source static scans และ current documentation/config tests.

- [x] 6. **ปิด privacy และ full regression gate** — เพิ่ม logger/console canary tests, ยืนยัน audit/browser/tool output ไม่รั่ว identity หรือ credential และรัน build, unit, integration, architecture, guard, secret และ traceability gates ครบ
     Satisfies: REQ-7.12-7.18, REQ-8.1-8.8, REQ-9.26-9.27. Depends on: 1-5. Verify: commands ทั้งหมดใน design Testing Strategy ต้องผ่านตามผลจริง; infrastructure failure รายงาน failed หรือ blocked.
หรือใช้ `/spec-implement all`. หยุด review ที่ task boundary ตาม `TASK_PROTOCOL.md`
