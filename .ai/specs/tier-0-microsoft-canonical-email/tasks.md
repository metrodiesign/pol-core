# Implementation Tasks: Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)

> Status: approved 2026-08-23

แต่ละ task เป็น slice ที่ implement และตรวจได้ในรอบเดียวพร้อม tests. Feature นี้แชร์ canonicalizer,
Admin identity transaction และ migration cutover จึงต้องทำตาม dependency และคง approval gate ที่ task boundary

## Implementation checklist

- [x] 1. **สร้าง canonical email identity foundation** — เพิ่ม BCL-based `WorkforceEmail`, บังคับ User subject/email/key invariants และ persist nullable unique `WorkforceEmailKey` โดยไม่เพิ่ม dependency
  Satisfies: REQ-2.10-2.17, REQ-2.29-2.31, REQ-3.1-3.5, REQ-4.17, REQ-4.22, REQ-4.24-4.25, REQ-8.8. Verify: Admin domain tests และ real-SQL canonical-key collation/uniqueness tests.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed
  - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 176 passed / 0 failed
  - viewports: n/a — backend logic/data only
  - deviations: none

- [x] 2. **Resolve, bind หรือ JIT Admin แบบ atomic** — ใช้ candidate set เดียวภายใต้ identity mutation lock, bind Active unbound owner, reject suspended/divergent owners, create roleless Scoped JIT และ commit identity audit พร้อม mutation
  Satisfies: REQ-3.3-3.13, REQ-4.1-4.25, REQ-5.1-5.12, REQ-7.1-7.6, REQ-7.10-7.14, REQ-7.19-7.21, REQ-9.19-9.23. Depends on: 1. Verify: Admin handler/domain tests และ real-SQL conflict, rollback, bind/JIT concurrency tests.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed
  - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 176 passed / 0 failed
  - viewports: n/a — backend logic/data only
  - deviations: none

- [x] 3. **เปลี่ยน Tier 0 OIDC callback เป็น canonical email** — ตรวจ exact tenant และ claim precedence หลัง middleware validation, ignore `roles`/`oid`, ส่ง Microsoft callback เข้า resolver เดียว และสร้าง session เฉพาะ Active resolved Admin
  Satisfies: REQ-1 (all criteria), REQ-2 (all criteria), REQ-4.19-4.21, REQ-7.5-7.9, REQ-7.12-7.18, REQ-8.1-8.4, REQ-9.11-9.18, REQ-9.20, REQ-9.24. Depends on: 1-2. Verify: Host OIDC, callback, session, failure-classification และ provider-regression tests.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed
  - viewports: n/a — server-side OIDC callback only
  - deviations: none

- [x] 4. **Migrate oid subjects แบบ fail closed** — เพิ่ม snapshot/state manifest, privileged console migrator, completed-state startup invariant gate, guarded Down และ migrate-container sequencing พร้อม atomic preflight
  Satisfies: REQ-6.1-6.18, REQ-6.20-6.28, REQ-9.25. Depends on: 1-2. Verify: real-SQL valid/no-op/rejection/idempotency/rollback tests และ migrate-service non-zero gate test.

  Evidence:
  - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 176 passed / 0 failed, including 8 migration SQL tests
  - test: `bash docker/migrate-entrypoint.test.sh` -> 56 passed / 0 failed
  - test: `dotnet ef migrations has-pending-model-changes --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api --no-build` -> no pending model changes
  - viewports: n/a — migration/tool only
  - deviations: none

- [x] 5. **Retire oid pre-provision และปรับ operational documentation** — ลบ endpoint/handler/audit wiring เดิม, คง normal `404`, ลบ current App Role prerequisite และ document claim rules, backup, maintenance, rollback cutoff และ email rename/reuse risk
  Satisfies: REQ-6.19, REQ-8.1-8.7, REQ-9.1-9.10, REQ-9.28. Depends on: 3-4. Verify: retired-route no-mutation test, production-source static scans และ current documentation/config tests.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed
  - test: `scripts/check-rename-identifiers.sh` -> passed / no retired live-code identifier
  - test: `set -a; source .env.prod.example; set +a; docker compose -f docker-compose.prod.yml config -q` -> passed
  - viewports: n/a — API/config/docs only
  - deviations: none

- [x] 6. **ปิด privacy และ full regression gate** — เพิ่ม logger/console canary tests, ยืนยัน audit/browser/tool output ไม่รั่ว identity หรือ credential และรัน build, unit, integration, architecture, guard, secret และ traceability gates ครบ
  Satisfies: REQ-7.12-7.18, REQ-8.1-8.8, REQ-9.26-9.27. Depends on: 1-5. Verify: commands ทั้งหมดใน design Testing Strategy ต้องผ่านตามผลจริง; infrastructure failure รายงาน failed หรือ blocked.

  Evidence:
  - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> passed / 0 warnings / 0 errors
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,929 passed / 0 failed
  - test: `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 176 passed / 0 failed
  - test: Bash CI guard loop over `.claude/hooks/tests/*.test.sh`, Docker entrypoint tests and release-evidence test -> 10 scripts passed / 0 failed
  - test: `SECRET_GUARD_SKIP='' .ai/bin/check-secrets.sh --all` -> passed
  - test: `scripts/spec-trace.sh tier-0-microsoft-canonical-email` -> 183/183 criteria covered; EARS lint passed
  - viewports: n/a — backend/security regression only
  - deviations: none

## Suggested execution batches

Feature นี้ coupled: tasks 1-6 แชร์ domain invariant, persistence และ callback contract. รันทั้งหมด
ใน session เดียวตาม dependency; ไม่มี `Batch:` tag เพราะแต่ละ task มี verification boundary ใหญ่ของตนเอง

```bash
scripts/pane-loop.sh tier-0-microsoft-canonical-email all-in-one
```

หรือใช้ `/spec-implement all`. หยุด review ที่ task boundary ตาม `TASK_PROTOCOL.md`
