# Implementation Tasks: Entra Scoped Pre-provision

> Status: approved 2026-08-19

แต่ละ task เป็น slice ที่ตรวจได้เองและต้อง implement พร้อม tests ในรอบเดียว. Feature นี้แชร์
transaction, persistence และ auth flow จึงควรรันทุก task ใน session เดียวตามลำดับ dependency

- [x] 1. **ตรึง Workforce tenant และทำ migration ปลอดภัย** — validate public-cloud Authority, persist immutable singleton ก่อน listen และ canonicalize existing subjects ด้วย exact SQL guards
  Satisfies: REQ-1.16-1.20, REQ-6.5, REQ-6.14.
  Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj` และ `dotnet test tests/Integration.Tests/Integration.Tests.csproj`.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,911 passed, 0 failed, 0 skipped
  - test: ephemeral SQL 2025 bootstrap + `dotnet test pol-core.slnx --no-build --filter "Category=Integration"` -> Architecture 4 + Integration 168 passed, 0 failed, 0 skipped
  - viewports: n/a — logic/API-only
  - deviations: none

- [x] 2. **ผูก Microsoft identity แบบ atomic พร้อม tamper-evident audit** — เพิ่ม command/handler, one-time binding, Active Super lease และ platform hash chain โดยคง authorization state
  Satisfies: REQ-1.2-1.4, REQ-1.12, REQ-1.14, REQ-1.22-1.23, REQ-2.1-2.12, REQ-2.16, REQ-5.1-5.6, REQ-5.10-5.12, REQ-6.7, REQ-6.15.
  Depends on: 1.
  Verify: `dotnet test tests/Admins.Tests/Admins.Tests.csproj` และ `dotnet test tests/Governance.Tests/Governance.Tests.csproj`.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> Admins 118 + Governance 8 passed, 0 failed, 0 skipped
  - test: ephemeral SQL 2025 bootstrap + `dotnet test pol-core.slnx --no-build --filter "Category=Integration"` -> transaction, rollback และ audit production paths passed
  - viewports: n/a — logic/API-only
  - deviations: none

- [x] 3. **ทำ retry, no-op และ concurrent binding ให้ deterministic** — reuse operation records, enforce ETag/idempotency ordering และ map races โดยไม่เกิด partial write
  Satisfies: REQ-1.8-1.9, REQ-1.15, REQ-2.13-2.18, REQ-3.1-3.9, REQ-6.10-6.11, REQ-6.15.
  Depends on: 2.
  Verify: `dotnet test tests/Admins.Tests/Admins.Tests.csproj` และ `dotnet test tests/Integration.Tests/Integration.Tests.csproj`.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> Admins 118 passed, 0 failed, 0 skipped
  - test: ephemeral SQL 2025 bootstrap + `dotnet test pol-core.slnx --no-build --filter "Category=Integration"` -> concurrency, replay retention และ rollback cases passed
  - viewports: n/a — logic/API-only
  - deviations: none

- [x] 4. **เปิด Super-only pre-provision HTTP contract** — เพิ่ม PUT endpoint, security/validation gates, minimal response/ETag, OpenAPI และ RFC 9457 stable codes
  Satisfies: REQ-1.1, REQ-1.5-1.15, REQ-1.17, REQ-1.21, REQ-1.24, REQ-2.3, REQ-2.10-2.14, REQ-2.18-2.19, REQ-3.9, REQ-5.8-5.9, REQ-6.8-6.9, REQ-6.16.
  Depends on: 1-3.
  Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj`.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> Hosts 634 passed, 0 failed, 0 skipped
  - test: `dotnet build pol-core.slnx --no-restore -warnaserror -m:1 /nodeReuse:false -p:UseSharedCompilation=false` -> 0 warnings, 0 errors
  - viewports: n/a — API contract only
  - deviations: none

- [x] 5. **บังคับ Entra `tid`/`oid` และลด identity leakage หลัง login** — ใช้ canonical tenant-local claims, ตัด external subject จาก session/audits และรักษา login เดิม
  Satisfies: REQ-1.17, REQ-4.1-4.12, REQ-5.7-5.8, REQ-5.13, REQ-6.1-6.6, REQ-6.12-6.13.
  Depends on: 1-2.
  Verify: `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj`.

  Evidence:
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> Hosts 634 passed, including OIDC negative contracts
  - test: Browser Lab happy path -> pre-provision, replay, employee login, session และ merchant scope passed
  - viewports: n/a — authentication flow only
  - deviations: employee Lab account เป็น tenant-local Entra B2B Guest; production-like Member ยังไม่ได้ตรวจ manual

- [x] 6. **ปิด SQL, security และ Lab acceptance gate** — พิสูจน์ migration, races, rollback, audit chain, replay retention, full contract และ employee login/negative control
  Satisfies: REQ-1.18-1.20, REQ-1.22-1.24, REQ-2.15-2.17, REQ-3.1-3.9, REQ-5.1, REQ-5.7-5.13, REQ-6.7-6.16.
  Depends on: 1-5.
  Verify: full build/unit/integration/spec-trace commands ใน design พร้อม Browser Lab checklist และ Entra sign-in evidence.

  Evidence:
  - test: `dotnet build pol-core.slnx --no-restore -warnaserror -m:1 /nodeReuse:false -p:UseSharedCompilation=false` -> 0 warnings, 0 errors
  - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1,911 passed, 0 failed, 0 skipped
  - test: ephemeral SQL 2025 bootstrap + `dotnet test pol-core.slnx --no-build --filter "Category=Integration"` -> Architecture 4 + Integration 168 passed, 0 failed, 0 skipped
  - test: `scripts/spec-trace.sh entra-scoped-preprovision` -> 93 criteria referenced, EARS lint passed
  - test: Browser Lab unbound Microsoft account -> `/login-error?reason=not-provisioned`; valid Entra callback denied without provisioning
  - viewports: n/a — authentication flow only
  - deviations: foreign/missing `tid`/`oid` ใช้ automated contract coverage; SPA error copy ยังระบุ Google แม้ flow เป็น Microsoft และ source อยู่นอก repo นี้

## Suggested execution batches

Feature นี้ coupled: ทุก task แชร์ tenant snapshot, Admin transaction, audit และ login path. ใช้
session เดียวเพื่อคง context และรันตาม dependency:

```bash
scripts/pane-loop.sh entra-scoped-preprovision all-in-one
```

หรือใช้ `/spec-implement all`. ไม่มี `Batch:` tag เพราะแต่ละ task ใหญ่และมี verification boundary ของตนเอง
