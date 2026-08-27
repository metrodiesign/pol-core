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

- [x] 2. Update documentation — สลับ `PaymentOrchestration` -> `VCentralPay` ใน live docs (`README.md`,
     `docs/runbooks/local-dev-run.md`, `docs/runbooks/deploy-self-host.md`, `docs/reference/db-connection-and-rls.md`)
     + `.ai/specs/*` historical (tenant, production-hardening, producer-google-sso) โดย **คงคำ stale
     "schema `producer`/`admin`"** ใน `production-hardening/design.md` ไว้ ; และ **เพิ่ม cutover note ของ DB-rename**
     ใน `docs/runbooks/local-dev-run.md`. "done" = docs อ้างชื่อ DB ใหม่ + มี note.
     Satisfies: REQ-5 (all). Depends on: none. Batch: B1.

- [x] 3. Reset-only cutover + full verification — recreate dev `:11433` และ integration `:11434` เป็น DB
     `VCentralPay` (`docker compose down -v && up -d`), migrate (auto-boot หรือ `dotnet ef database update`),
     ยืนยัน `GET /health/ready` healthy + query จริงผ่าน, รัน integration suite (source `.env.integration`), push
     branch ให้ CI (fresh-from-zero) เขียว. "done" = ไม่มี committed literal เหลือ + ทุก suite/CI เขียวบนชื่อใหม่.
     Satisfies: REQ-3 (all), REQ-4, REQ-6.3, REQ-6.4. Depends on: 1, 2.
> `scripts/pane-loop.sh db-rename-vcentralpay 1+2`. task 3 = runtime cutover ทำหลัง 1+2 เสร็จ.
