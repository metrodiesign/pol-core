# Requirements: Rename database `PaymentOrchestration` -> `VCentralPay`

> Status: approved 2026-07-08
> Notes:, amended 2026-07-08
> Derived from design.md (design-first) — แต่ละ REQ อ้าง section ต้นทางใน design

## Overview

ปิด axis ที่สองของ rebrand VCentralPay: เปลี่ยน **ชื่อ database (SQL Server catalog)** จาก
`PaymentOrchestration` เป็น `VCentralPay` ต่อจาก PR #68 ที่ rename SQL schema `producer` -> `VCentralPay`
ไปแล้ว. ชื่อ database เป็นแกนแยกจาก schema และ runtime path parameterize ผ่านตัวแปรอยู่แล้ว จึงเป็นการ
propagate ค่าใหม่ผ่าน configuration surface ที่ยัง hardcode literal โดยไม่แตะ logic — สอดคล้อง
`PROJECT_CONTEXT.md` (platform เดียว, config-only change ไม่กระทบเส้นทางเงิน)

## REQ-1: Database name replaced across configuration surfaces
**User Story:** As an operator, I want every committed configuration surface to reference database `VCentralPay` instead of `PaymentOrchestration`, so that all hosts, CI, and tooling connect to the correctly named catalog.
**Acceptance Criteria (EARS):** _(design: Architecture Overview, Data Models & Interfaces, Technology Decisions #1)_
- 1.1  THE SYSTEM SHALL reference `Database=VCentralPay` in every committed connection string in `src/Hosts/Api/appsettings.json`, `src/Hosts/Api/appsettings.Development.json`, `src/Hosts/Api/appsettings.Development.json.example`, `src/Hosts/Worker/appsettings.json`, and `src/Hosts/Worker/appsettings.Development.json.example`.
- 1.2  THE SYSTEM SHALL set the database-name value to `VCentralPay` in `.env.example`, `.env.integration`, and `.env.prod.example`.
- 1.3  THE SYSTEM SHALL set the database-name value to `VCentralPay` in `docker-compose.yml`, all three defaults in `docker-compose.prod.yml`, and `.github/workflows/ci.yml`.
- 1.4  WHERE the integration test harness has no `POL_DB` environment variable, THE SYSTEM SHALL fall back to `VCentralPay` in `tests/Integration.Tests/IntegrationDb.cs`.
- 1.5  THE SYSTEM SHALL keep the connection-string format unchanged except the `Database=` value.

## REQ-2: Orthogonal identifiers preserved
**User Story:** As a maintainer, I want the rename limited to the catalog name, so that unrelated identifiers stay stable and the blast radius stays minimal.
**Acceptance Criteria (EARS):** _(design: Architecture Overview "ขอบเขตชัดเจน", Technology Decisions #3)_
- 2.1  THE SYSTEM SHALL NOT change connection-string keys (`Producer`/`Admin`/`Worker`/`Migrator`), DB principals (`pol_app`/`pol_admin`/`pol_worker`), container `pol-db`, volumes `pol-db-data`/`sql-data`, class `ProducerDbContext`, module names, or SQL schema `VCentralPay`.
- 2.2  THE SYSTEM SHALL change only the VALUES of variables `DB_NAME`/`POL_DB`/`$(DbName)`, not their names.
- 2.3  THE SYSTEM SHALL leave the already-parameterized surfaces (`docker/bootstrap/01-principals.sql`, `docker/entrypoint.sh`, `docker/migrate-entrypoint.sh`) unmodified.

## REQ-3: Reset-only cutover
**User Story:** As a developer, I want a documented reset-only cutover, so that a fresh `VCentralPay` database is created and migrated without stranded data.
**Acceptance Criteria (EARS):** _(design: Sequence Diagrams "Cutover reset-only", Technology Decisions #2, Error Handling)_
- 3.1  WHEN the operator runs `docker compose down -v && docker compose up -d`, THE SYSTEM SHALL create database `VCentralPay` via `pol-db-init` and the init step SHALL exit 0.
- 3.2  WHEN migrations are applied against the new catalog, THE SYSTEM SHALL place schema `VCentralPay` tables inside database `VCentralPay`.
- 3.3  WHILE a host runs against `VCentralPay`, THE SYSTEM SHALL return healthy on `GET /health/ready` and serve a real query.
- 3.4  WHERE the cutover procedure is documented, THE SYSTEM SHALL instruct the operator to update local `.env` to `VCentralPay` before boot.
- 3.5  IF the volume is not wiped during cutover, THEN database `PaymentOrchestration` remains an orphan and `VCentralPay` is empty; THEREFORE the runbook SHALL require `docker compose down -v`.

## REQ-4: No residual literal (verification gate)
**User Story:** As a reviewer, I want a mechanical gate proving no host is left pointing at the old catalog, so that the rename is provably complete.
**Acceptance Criteria (EARS):** _(design: Testing Strategy B1, Error Handling)_
- 4.1  THE SYSTEM SHALL contain zero committed occurrences of `PaymentOrchestration` in configuration/connection surfaces (appsettings, env templates, docker/CI, test fallback) under `grep -rniI --exclude-dir={bin,obj,.git,node_modules}`, excepting the gitignored local `.env` and documentation that explains the rename itself (the `docs/runbooks/local-dev-run.md` cutover note, which must name the old catalog for the `ALTER DATABASE ... MODIFY NAME` path, and this feature's own spec dir `.ai/specs/db-rename-vcentralpay/`).

## REQ-5: Documentation updated
**User Story:** As a new contributor, I want the docs to name the current database, so that onboarding and runbooks are accurate.
**Acceptance Criteria (EARS):** _(design: Architecture Overview, Data Models & Interfaces note)_
- 5.1  THE SYSTEM SHALL update live docs (`README.md`, `docs/runbooks/*`, `docs/reference/*`) to reference `VCentralPay`.
- 5.2  WHERE opted in, THE SYSTEM SHALL update `.ai/specs/*` historical references to `VCentralPay`, while leaving the pre-existing stale `schema producer/admin` wording in `.ai/specs/production-hardening/design.md` untouched.
- 5.3  THE SYSTEM SHALL add a DB-rename cutover note to `docs/runbooks/local-dev-run.md`.

## REQ-6: Build, test, and CI green on the new name
**User Story:** As an engineer, I want all builds and suites to pass on the renamed catalog, so that the change is safe to merge.
**Acceptance Criteria (EARS):** _(design: Testing Strategy B5-B7)_
- 6.1  WHEN `dotnet build pol-core.slnx` runs, THE SYSTEM SHALL build with zero errors.
- 6.2  WHEN the unit and architecture suites run (Hosts.Tests, Producer.Tests, Architecture.Tests), THE SYSTEM SHALL pass.
- 6.3  WHEN the integration suite runs against a recreated `:11434` database named `VCentralPay`, THE SYSTEM SHALL pass.
- 6.4  WHEN CI runs on a fresh clone, THE SYSTEM SHALL bootstrap `VCentralPay` from zero and all required checks SHALL pass.

## Edge Cases & Open Questions

- **Future prod with real data (out of scope):** เมื่อมี prod DB ที่มีข้อมูล การ rename ต้องใช้
  `ALTER DATABASE [PaymentOrchestration] MODIFY NAME = [VCentralPay]` + backup ก่อน deploy default ใหม่
  (bootstrap จะสร้าง `VCentralPay` ว่างและทิ้งข้อมูลเดิม). งานนี้ pre-prod จึงใช้ reset-only — path prod
  ระบุไว้ใน runbook เป็น ceiling
- **Local `.env` (gitignored):** ไม่เข้า git — ความรับผิดชอบของ operator ระหว่าง cutover (REQ-3.4);
  grep gate (REQ-4.1) จึงยกเว้นไฟล์นี้
