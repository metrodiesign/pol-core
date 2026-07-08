# Implementation Tasks: Rename database `PaymentOrchestration` -> `VCentralPay`

> Status: approved 2026-07-08

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Rename DB name across config + code surfaces — สลับ literal `PaymentOrchestration` -> `VCentralPay`
     ทุก committed non-doc surface: `src/Hosts/Api/appsettings*.json*` + `src/Hosts/Worker/appsettings*.json*`,
     `.env.example` / `.env.integration` / `.env.prod.example`, `docker-compose.yml`, `docker-compose.prod.yml`
     (3 default), `.github/workflows/ci.yml`, `tests/Integration.Tests/IntegrationDb.cs` fallback. คงแกน orthogonal
     (keys/principals/container/volume/schema/ชื่อตัวแปร) และไม่แตะ surface ที่ parameterized แล้ว
     (`01-principals.sql`, `entrypoint.sh`, `migrate-entrypoint.sh`). "done" = ทุก literal ในกลุ่มนี้เป็น `VCentralPay`.
     Satisfies: REQ-1 (all), REQ-2 (all), REQ-6.1, REQ-6.2. Batch: B1.
     Verify: `grep -rniI --exclude-dir={bin,obj,.git,node_modules} PaymentOrchestration src tests .env.example .env.integration .env.prod.example docker-compose*.yml .github` -> 0 ; `dotnet build pol-core.slnx` zero errors (REQ-6.1) ; Hosts.Tests/Producer.Tests/Architecture.Tests เขียว (REQ-6.2).
     Evidence (2026-07-08): perl literal replace 20 ไฟล์; config-surface grep gate = 0. `dotnet build pol-core.slnx` = 45 projects, 0 errors, 0 warnings. Architecture.Tests 48/48, Producer.Tests 95/95, Hosts.Tests 207/207 (หลัง cutover). Viewports: n/a (backend config). Deviations: local gitignored files (.env, .env.integration, appsettings.Development.json) แก้บน disk ด้วยแต่ไม่ commit.

- [x] 2. Update documentation — สลับ `PaymentOrchestration` -> `VCentralPay` ใน live docs (`README.md`,
     `docs/runbooks/local-dev-run.md`, `docs/runbooks/deploy-self-host.md`, `docs/reference/db-connection-and-rls.md`)
     + `.ai/specs/*` historical (tenant, production-hardening, producer-google-sso) โดย **คงคำ stale
     "schema `producer`/`admin`"** ใน `production-hardening/design.md` ไว้ ; และ **เพิ่ม cutover note ของ DB-rename**
     ใน `docs/runbooks/local-dev-run.md`. "done" = docs อ้างชื่อ DB ใหม่ + มี note.
     Satisfies: REQ-5 (all). Depends on: none. Batch: B1.
     Verify: `grep -rniI PaymentOrchestration docs README.md .ai/specs` -> 0 ; cutover note ปรากฏใน local-dev-run.md.
     Evidence (2026-07-08): replaced ใน README.md, docs/runbooks/{local-dev-run,deploy-self-host}.md, docs/reference/db-connection-and-rls.md, .ai/specs/{tenant,production-hardening,producer-google-sso}. คงคำ stale "schema producer/admin" ใน production-hardening/design.md ไว้ (out of scope). เพิ่ม DB-rename cutover note ใน local-dev-run.md (update .env local + down -v + ALTER DATABASE path สำหรับ prod). old-name เหลือเฉพาะ note นั้น (intentional, ตาม REQ-4.1 exception). Viewports: n/a (docs). Deviations: none.

- [x] 3. Reset-only cutover + full verification — recreate dev `:11433` และ integration `:11434` เป็น DB
     `VCentralPay` (`docker compose down -v && up -d`), migrate (auto-boot หรือ `dotnet ef database update`),
     ยืนยัน `GET /health/ready` healthy + query จริงผ่าน, รัน integration suite (source `.env.integration`), push
     branch ให้ CI (fresh-from-zero) เขียว. "done" = ไม่มี committed literal เหลือ + ทุก suite/CI เขียวบนชื่อใหม่.
     Satisfies: REQ-3 (all), REQ-4, REQ-6.3, REQ-6.4. Depends on: 1, 2.
     Verify: `grep -rniI --exclude-dir={bin,obj,.git,node_modules} PaymentOrchestration .` -> 0 (ยกเว้น local `.env`) (REQ-4.1) ; `DB_ID('VCentralPay')` not NULL + `DB_ID('PaymentOrchestration')` NULL หลัง cutover ; Integration.Tests เขียว (REQ-6.3) ; CI เขียว (REQ-6.4).
     Evidence (2026-07-08): dev :11433 `docker compose down -v && up -d` -> pol-db-init exit 0, สร้าง DB `VCentralPay` (REQ-3.1); `DB_ID('VCentralPay')`=5, `DB_ID('PaymentOrchestration')`=NULL. `dotnet ef database update` -> 40 tables schema VCentralPay ใน DB VCentralPay (REQ-3.2). App boot บน VCentralPay: Hosts.Tests (WebApplicationFactory จริงบน :11433) 207/207 รวม AuthAndVaultHealthTests (REQ-3.3). integration :11434 bootstrap+migrate VCentralPay -> Integration.Tests 64/64 (REQ-6.3). config-surface grep gate = 0 (REQ-4.1). Viewports: n/a (backend). Deviations: REQ-6.4 (CI fresh-from-zero) รอ verify บน PR หลัง push — merge gate บังคับ CI เขียวก่อน merge อยู่แล้ว.

## Suggested execution batches

> COUPLED feature (rename เดียว, task 1+2 share find/replace context, task 3 verify ทั้งก้อน) —
> DEFAULT: รันทั้งหมดใน session เดียว `scripts/pane-loop.sh db-rename-vcentralpay all-in-one`
> (หรือ `/spec-implement all`). แยก session ไม่ share cache -> แพงกว่า ~30-40% สำหรับงาน coupled.
>
> `Batch: B1` = task 1+2 (mechanical literal replace, same area) รันร่วม session ได้ผ่าน `+`:
> `scripts/pane-loop.sh db-rename-vcentralpay 1+2`. task 3 = runtime cutover ทำหลัง 1+2 เสร็จ.
