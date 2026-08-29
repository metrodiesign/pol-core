# Requirements: external-sim-separate-containers

> Status: unknown
> Scope: แยก simulated upstream databases `hippodb`/`mammothdb` (spec `products-sp-gateway`) ออกจาก
> SQL Server instance เดียวกับ `VCentralPay` (container `pol-db`, :11433) เป็น 2 SQL Server container
> ของตัวเอง (`hippo-db` :11434, `mammoth-db` :11435) ทุก environment (local compose, CI GitHub+GitLab,
> prod parameterized) — อ้างแผนที่ user approve ผ่าน plan mode (plan file:
> `~/.claude/plans/docker-hippodb-cozy-clock.md`)
> Supersedes: `products-sp-gateway` REQ-3.4 ("ไม่เพิ่ม env var ใหม่ — connection string จำลอง derive
> จากของที่มีอยู่") — closed spec, ไม่แก้ย้อนหลัง (ดูเหตุผลใน REQ-4 ด้านล่าง)
> Superseded (บางส่วน): REQ-2.1 ถึง REQ-2.5 เฉพาะส่วนที่ระบุ principal `pol_app` ถูกแทนที่โดย spec
> `sim-db-separate-logins` (`.pipeline/sim-db-separate-logins/spec.md`, 2026-08-05) — sim instance
> ทั้งสองเลิกใช้ `pol_app` (แชร์กับ VCentralPay) แล้ว: `hippodb` ใช้ login `hippo_app` รับ sqlcmd
> variable `HIPPO_APP_PASSWORD`, `mammothdb` ใช้ `mammoth_app` รับ `MAMMOTH_APP_PASSWORD` คนละ password
> กัน และ bootstrap ลบ `pol_app` เดิมทิ้งเองแบบ idempotent — spec นี้ปิดแล้ว ข้อความ REQ ด้านล่างคงไว้
> ตามที่ approve ไม่แก้ย้อนหลัง

## บริบท

`hippodb`/`mammothdb` (external SP gateway ของ `Products` module) ถูกสร้างโดย
`docker/bootstrap/02-external-sim.sql` บน SQL Server instance เดียวกับ `VCentralPay` — ขัดกับ topology
จริงที่ spec `products-sp-gateway` ตั้งใจไว้แต่แรก ("simulated upstream on separate servers") และทำให้
`src/Hosts/Api/Program.cs` มี `PostConfigure<SpDocumentOptions>` ที่ derive connection string จาก
`ConnectionStrings:App` โดยเปลี่ยนแค่ `InitialCatalog` — สมมติฐานที่ใช้ไม่ได้อีกต่อไปเมื่อแยก server จริง

งานนี้แยก `hippodb`/`mammothdb` ออกเป็นคนละ SQL Server container (คนละ "server" ในความหมายเดียวกับที่
`SpDocumentOptions.MotorConnectionString`/`NonMotorConnectionString` อ้างถึง) ทุก environment: local
compose, CI (GitHub Actions + GitLab CI), และ prod (parameterize ผ่าน env var, ไม่เพิ่ม container —
DB tier ของ prod เป็น host แยกอยู่แล้วตาม `docs/runbooks/deploy-self-host.md`)

## REQ-1: Topology — hippodb/mammothdb เป็น container/server แยกทุก environment

**User Story:** ในฐานะทีมพัฒนา ฉันต้องการให้ topology ของ sim DB ตรงกับที่ spec `products-sp-gateway`
ตั้งใจไว้แต่แรก เพื่อให้โค้ดที่พึ่ง connection string แยกไม่ถูกซ่อนด้วย default ที่ผิด (server เดียวกัน)

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL รัน `hippodb` บน SQL Server container ของตัวเอง (service `hippo-db`, container
  name `pol-hippo-db`, พอร์ต `11434:1433`) แยกจาก `pol-db` (:11433) ในทุก local environment
  (`docker-compose.yml`)
- 1.2 THE SYSTEM SHALL รัน `mammothdb` บน SQL Server container ของตัวเอง (service `mammoth-db`,
  container name `pol-mammoth-db`, พอร์ต `11435:1433`) แยกจาก `pol-db` และแยกจาก `hippo-db`
- 1.3 THE SYSTEM SHALL NOT ใช้ volume ถาวรสำหรับ `hippo-db`/`mammoth-db` — ephemeral (bootstrap
  idempotent สร้าง+seed ใหม่ทุกครั้งที่ container ถูกสร้างใหม่ ไม่มีอะไรต้อง persist ข้าม restart)
- 1.4 THE SYSTEM SHALL ใช้ image `mcr.microsoft.com/mssql/server:2025-latest` เดียวกับ `pol-db` สำหรับ
  ทั้ง `hippo-db` และ `mammoth-db` พร้อม healthcheck รูปแบบเดียวกัน (`sqlcmd ... -Q 'SELECT 1'`)
- 1.5 THE SYSTEM SHALL ใช้ `MSSQL_SA_PASSWORD` ค่าเดียวกันร่วมทั้ง 3 instance (`pol-db`, `hippo-db`,
  `mammoth-db`) ในทุก environment (local/CI/prod)

## REQ-2: Bootstrap ต่อ instance, self-contained

**User Story:** ในฐานะ maintainer ฉันต้องการให้แต่ละ instance มี bootstrap script ของตัวเองที่รันได้
อิสระ เพื่อให้สอดคล้องกับ topology จริงที่แต่ละ server เป็นระบบต้นทางคนละระบบ ไม่พึ่งพากัน

**Acceptance Criteria (EARS):**

> Superseded (บางส่วน) โดย `sim-db-separate-logins`: ทุกจุดที่ 2.1-2.5 เขียนว่า principal/`LOGIN` ของ sim
> คือ `pol_app` และ sqlcmd variable คือ `POL_APP_PASSWORD` ปัจจุบันคือ `hippo_app`/`HIPPO_APP_PASSWORD`
> (ไฟล์ 02) และ `mammoth_app`/`MAMMOTH_APP_PASSWORD` (ไฟล์ 03) — โครงสร้างข้ออื่นของ REQ-2 ไม่เปลี่ยน

- 2.1 THE SYSTEM SHALL มี `docker/bootstrap/02-hippo-sim.sql` แบบ idempotent สร้าง database
  `hippodb`, ตาราง `dbo.Documents`, unique index `UX_Documents_DocumentNo`, procedure
  `usp_Motor_SearchDocument`, principal `pol_app` (`LOGIN` + `USER`), `GRANT EXECUTE`, seed 200 แถว,
  และ self-check — รันได้อิสระบน instance ของตัวเองโดยไม่ต้องมี script อื่นรันมาก่อนบน instance นั้น
- 2.2 THE SYSTEM SHALL มี `docker/bootstrap/03-mammoth-sim.sql` โครงเดียวกันสำหรับ `mammothdb`/
  `usp_NonMotor_SearchDocument` — รันได้อิสระบน instance ของตัวเองโดยไม่ต้องมี script อื่นรันมาก่อน
  รวมถึงสร้าง `LOGIN pol_app` ของตัวเอง (instance นี้ไม่มี principal จาก `01-principals.sql` มาก่อน
  ต่างจาก `hippodb`/`mammothdb` เดิมที่อยู่ instance เดียวกับ `pol-db`)
- 2.3 THE SYSTEM SHALL ลบ `docker/bootstrap/02-external-sim.sql` — ถูกแทนที่ทั้งหมดโดย 2.1/2.2
- 2.4 THE SYSTEM SHALL รับ sqlcmd variable `POL_APP_PASSWORD` ในทั้ง `02-hippo-sim.sql` และ
  `03-mammoth-sim.sql` เพื่อสร้าง `LOGIN pol_app WITH PASSWORD = N'$(POL_APP_PASSWORD)', CHECK_POLICY =
  ON` แบบเดียวกับ `01-principals.sql:29-31`
- 2.5 THE SYSTEM SHALL รัน `01-principals.sql` (ที่ `pol-db`) -> `02-hippo-sim.sql` (ที่ `hippo-db`) ->
  `03-mammoth-sim.sql` (ที่ `mammoth-db`) ตามลำดับนี้ ผ่าน entrypoint chain เดียวของ service
  `pol-db-init` ใน local compose
- 2.6 IF self-check ท้ายสคริปต์ล้มเหลว (object ไม่ครบ, row count ผิด, collation ผิด) THEN THE SYSTEM
  SHALL `THROW` ด้วยข้อความ prefix ตรงชื่อไฟล์ตัวเอง (`02-hippo-sim: ...` ของ `02-hippo-sim.sql`,
  `03-mammoth-sim: ...` ของ `03-mammoth-sim.sql`) เพื่อให้ `sqlcmd -b` exit ไม่ใช่ 0 — ข้อความอ้างอิงชื่อ
  ไฟล์เดิม (`02-external-sim: ...`) ต้องไม่หลงเหลือ (ดู REQ-9.6)
- 2.7 THE SYSTEM SHALL NOT เปลี่ยนแปลงเนื้อ seed data (ค่า literal ใน `INSERT`/`UPDATE` statement)
  ของ `hippodb`/`mammothdb` แม้แต่ byte เดียวเทียบกับ `docker/bootstrap/02-external-sim.sql` เดิม —
  ข้อ 2.6 (ข้อความ self-check) และ REQ-3 (cross-database check ที่ถูกย้ายออก) อยู่นอกขอบเขตของ
  ข้อจำกัดนี้ (ไม่ใช่ seed data)

## REQ-3: Cross-instance invariants ยังถูก enforce ผ่าน integration test

**User Story:** ในฐานะ maintainer ฉันต้องการให้กติกาที่เคยพิสูจน์ด้วย SQL cross-database query (ตอนอยู่
instance เดียวกัน) ยังถูกพิสูจน์ต่อ แม้ตอนนี้อยู่คนละ server ที่ query ข้าม database ตรง ๆ ไม่ได้อีกแล้ว

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL ลบ cross-database self-check 2 บล็อกออกจาก SQL bootstrap เดิม (duplicate
  `DocumentNo` JOIN ข้าม `hippodb.dbo.Documents`/`mammothdb.dbo.Documents`, และ `SaleCode` roster
  identity ผ่าน `EXCEPT`) เพราะ query ข้าม server ทำไม่ได้อีกต่อไปโดยไม่มี linked server
- 3.2 THE SYSTEM SHALL มี integration test ใหม่ `tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs`
  (`[Trait("Category","Integration")]`) เปิด 2 connections แยกไปยัง `hippodb` และ `mammothdb` ด้วย
  principal `sa` (ไม่ใช่ `pol_app` — `pol_app` มีแค่ `EXECUTE` บน stored procedure ไม่มี `SELECT` บน
  `dbo.Documents` โดยตรง)
- 3.3 THE SYSTEM SHALL ยืนยันว่า `DocumentNo` ที่ไม่ใช่ NULL ไม่ซ้ำกันข้าม `hippodb`/`mammothdb`
  เทียบแบบ `StringComparer.OrdinalIgnoreCase`
- 3.4 THE SYSTEM SHALL ยืนยันว่า agent (`SaleCode`) ที่ปรากฏทั้งสองฝั่งมีค่า `SaleFullName`/
  `BrokerCode`/`BrokerName`/`ReferenceBranch`/`PolicyBranch` ตรงกันครบทั้ง 5 field แบบ null-safe
  (mirror semantics ของ `EXCEPT` เดิม — ค่า NULL เทียบกับค่าจริงถือว่าเป็น drift ไม่ใช่เท่ากัน)

## REQ-4: .NET application config — ไม่มี derive fallback

**User Story:** ในฐานะทีม API ฉันต้องการให้ connection string ของ sim DB มาจาก config เท่านั้น ไม่มี
fallback ที่สมมติว่า sim DB อยู่ instance เดียวกับ app database เพื่อให้โค้ดไม่โกหก topology จริง

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL ลบ `builder.Services.PostConfigure<SpDocumentOptions>(...)` ใน
  `src/Hosts/Api/Program.cs` ที่ derive `MotorConnectionString`/`NonMotorConnectionString` จาก
  `ConnectionStrings:App` โดยเปลี่ยน `InitialCatalog` เป็น `hippodb`/`mammothdb`
- 4.2 THE SYSTEM SHALL ให้ `SpDocumentOptions.MotorConnectionString`/`NonMotorConnectionString` มาจาก
  section configuration `SpDocument` เท่านั้น ไม่มี fallback หรือ derive อื่นใด
- 4.3 THE SYSTEM SHALL supersede `products-sp-gateway` REQ-3.4 ("ไม่เพิ่ม env var ใหม่ — connection
  string จำลอง derive จากของที่มีอยู่") เป็น: ทุก environment ประกาศ sim connection string ของตัวเอง
  ชัดเจนผ่าน env var/config ของตัวเอง ไม่มี derive — โดยไม่แก้ไฟล์ `products-sp-gateway/requirements.md`
  ย้อนหลัง (closed spec = historical record ตาม convention repo, precedent:
  `external-sim-realistic-branch-codes` REQ-4.4)
- 4.4 THE SYSTEM SHALL คง `products-sp-gateway` REQ-5.7 ทุกประการโดยไม่แก้ไข: `SpDocumentOptions`
  ต้องไม่ใช้ `.ValidateOnStart()` (Hosts.Tests boot จริง 17 hosts ที่ไม่แตะ dependency นี้)
- 4.5 WHILE `SpDocument:MotorConnectionString`/`SpDocument:NonMotorConnectionString` ไม่ถูกตั้งค่า,
  THE SYSTEM SHALL ให้ host boot สำเร็จ (ตาม REQ-4.4) และตอบ 503 (`UpstreamUnavailableException`) เมื่อ
  มี products search request เข้ามาเท่านั้น — ไม่ fail ตอน boot

## REQ-5: Seed data / contract non-regression

**User Story:** ในฐานะเจ้าของ contract test ฉันต้องการให้ seed data และจำนวนแถวที่ contract test พึ่งพา
ไม่เปลี่ยนแปลงจากการแยก container นี้ เพื่อไม่ให้ test เดิมพังจากการ refactor ที่ไม่เกี่ยวกับ business logic

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL คงจำนวนแถวทั้งหมดของ `hippodb.dbo.Documents` = 200 และ `mammothdb.dbo.Documents`
  = 200 หลัง bootstrap
- 5.2 THE SYSTEM SHALL คงจำนวนแถวที่มองเห็นได้จาก default search ของ `SaleCode = '77001'`: `hippodb` =
  42 แถว, `mammothdb` = 40 แถว
- 5.3 THE SYSTEM SHALL คง roster 6 agents (`SaleCode` 77001-77006) ครบทั้งสองฝั่ง หลัง bootstrap

## REQ-6: Prod deployment — parameterized + fail-fast

**User Story:** ในฐานะ operator ที่ deploy prod ฉันต้องการให้ connection ไปยัง sim DB tier ถูก
parameterize ชัดเจนและ fail-fast เมื่อ config ขาด แทนที่จะ derive แบบเงียบ ๆ เหมือนที่เคยทำใน dev

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL NOT เพิ่ม SQL container ใหม่ใน `docker-compose.prod.yml` — DB tier ของ sim
  (เหมือน DB tier หลัก) เป็น host แยกที่ infra/DBA จัดเตรียม ไม่ใช่ container ใน compose นี้
- 6.2 THE SYSTEM SHALL เพิ่ม environment variable `HIPPO_DB_SERVER`/`HIPPO_DB_PORT`/
  `MAMMOTH_DB_SERVER`/`MAMMOTH_DB_PORT` ให้ทั้ง service `migrate` และ `api` ใน
  `docker-compose.prod.yml` — server เป็น required (`${VAR:?...}`), port default `1433`
  (`${VAR:-1433}`)
- 6.3 IF `HIPPO_DB_SERVER` หรือ `MAMMOTH_DB_SERVER` ไม่ถูกตั้งตอน render `docker-compose.prod.yml`
  THEN THE SYSTEM SHALL fail ทันทีด้วยข้อความจาก compose `:?` operator (ก่อน container ใด ๆ start)
- 6.4 THE SYSTEM SHALL ให้ `docker/entrypoint.sh` ประกอบ `SpDocument__MotorConnectionString`/
  `SpDocument__NonMotorConnectionString` เองจาก `HIPPO_DB_SERVER`/`HIPPO_DB_PORT`/
  `MAMMOTH_DB_SERVER`/`MAMMOTH_DB_PORT` + secret แล้ว export เป็น environment variable ก่อน `exec`
  host (secret ไม่โผล่ใน compose file หรือ `docker inspect`) — pattern เดียวกับการประกอบ
  `ConnectionStrings__App`
- 6.5 WHILE `HIPPO_DB_SERVER` ไม่ถูกตั้ง (เช่น รัน image เดี่ยวนอก `docker-compose.prod.yml`), THE
  SYSTEM SHALL ข้าม export `SpDocument__*` (ไม่ fail boot) — REQ-4.5 ยังคุ้มครองพฤติกรรมนี้
- 6.6 THE SYSTEM SHALL ให้ `docker/migrate-entrypoint.sh` รอ (`wait_for_db`) แต่ละ server (DB หลัก,
  hippo, mammoth) ให้เชื่อมต่อได้ก่อนรัน bootstrap script ของ instance นั้น แล้วรัน `02-hippo-sim.sql`
  ที่ `HIPPO_DB_SERVER,HIPPO_DB_PORT` และ `03-mammoth-sim.sql` ที่ `MAMMOTH_DB_SERVER,MAMMOTH_DB_PORT`
  — ทั้งคู่ก่อนขั้นตอน `dotnet ef database update`

## REQ-7: CI mirror ทั้ง GitHub Actions และ GitLab CI

**User Story:** ในฐานะทีมพัฒนา ฉันต้องการให้ CI ทั้งสอง platform จำลอง topology ใหม่เหมือนกัน เพื่อให้
integration test เขียวสะท้อนสภาพจริงที่ prod จะเจอ

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL เพิ่ม service container `hippo` (พอร์ต `11434:1433`) และ `mammoth`
  (`11435:1433`) ในจ็อบ `dotnet-integration` ของ `.github/workflows/ci.yml` — image/env/healthcheck
  options เดียวกับ service `sql` ที่มีอยู่แล้ว
- 7.2 THE SYSTEM SHALL เพิ่ม environment variable `POL_HIPPO_SQL_SERVER: localhost,11434` และ
  `POL_MAMMOTH_SQL_SERVER: localhost,11435` ให้จ็อบ `dotnet-integration`
- 7.3 THE SYSTEM SHALL แทนที่ step bootstrap sim เดิม (ยิง `02-external-sim.sql` ครั้งเดียวที่
  `localhost,11433`) ด้วย 2 คำสั่งแยก — ยิง `02-hippo-sim.sql` ที่ `localhost,11434` และ
  `03-mammoth-sim.sql` ที่ `localhost,11435` — ทั้งคู่หลัง step generate `POL_APP_PASSWORD`
- 7.4 THE SYSTEM SHALL เพิ่ม `HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER` placeholder ใน env ของ step
  "Validate docker-compose.prod.yml renders" (job `docker-build`) ของ `ci.yml`
- 7.5 THE SYSTEM SHALL mirror ข้อ 7.1-7.4 ใน `.gitlab-ci.yml`: เพิ่ม service alias `hippo`/`mammoth`
  (variables ชุดเดียวกับ service `sql`), variables `POL_HIPPO_SQL_SERVER: hippo` /
  `POL_MAMMOTH_SQL_SERVER: mammoth`, ขยาย wait-loop ให้ครบ 3 servers, แทน bootstrap step ด้วย 2
  คำสั่งต่อไฟล์ (พร้อม `-v POL_APP_PASSWORD`), และเพิ่ม `HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER`
  placeholder ใน render-check export block ของจ็อบ `package`

## REQ-8: เอกสารสะท้อน topology ใหม่

**User Story:** ในฐานะสมาชิกทีมใหม่ที่อ่านเอกสาร ฉันต้องการให้ diagram/คำอธิบาย topology ตรงกับ compose
จริง เพื่อไม่ให้เข้าใจผิดว่า sim DB ยังอยู่ container เดียวกับ DB หลัก

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL อัปเดต `docs/reference/products.md` ให้สะท้อนว่า `hippodb`/`mammothdb` อยู่คนละ
  container จาก DB หลัก และลบคำอธิบายที่บอกว่า connection string derive จาก `ConnectionStrings:App`
- 8.2 THE SYSTEM SHALL อัปเดต `docs/runbooks/local-dev-run.md` ให้สะท้อนจำนวน service ที่ยกจาก
  `docker compose up -d` (5 แทน 3), topology table เพิ่มแถว `hippo-db`/`mammoth-db`, และตัวอย่าง
  export `.env.integration` ใน §6 เพิ่ม `POL_HIPPO_SQL_SERVER`/`POL_MAMMOTH_SQL_SERVER`
- 8.3 THE SYSTEM SHALL อัปเดต `docs/runbooks/deploy-self-host.md` (หัวข้อ collation cutover) ให้แยก
  คำสั่ง drop/recreate ต่อ server (3 servers แทนคำสั่งเดียวที่ครอบ `VCentralPay`/`hippodb`/`mammothdb`
  บน server เดียว) และระบุ prerequisite ว่า sim DB tier เป็น host แยกที่ต้อง provision ก่อน
- 8.4 THE SYSTEM SHALL อัปเดต `docs/reference/db-connection-and-rls.md` (flow diagram ของ
  `GET /api/v1/products`) ให้สะท้อนว่า gateway ต่อออกคนละ server ผ่าน `SpDocument__*` ที่
  `docker/entrypoint.sh` เป็นผู้ประกอบ

## REQ-9: Definition of Done

**Acceptance Criteria (EARS):**
- 9.1 WHEN รัน `docker compose down -v && docker compose up -d` บนเครื่อง local, THE SYSTEM SHALL ให้
  `pol-db-init` จบ `Exited (0)` โดย self-check ผ่านทั้ง `hippodb` (:11434) และ `mammothdb` (:11435)
- 9.2 WHEN รัน `dotnet build pol-core.slnx --no-restore -warnaserror` และ
  `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`, THE SYSTEM SHALL ผ่านทั้งหมด
  0 error / 0 warning
- 9.3 WHEN รัน `dotnet test --filter "Category=Integration"` กับ stack ที่แยก container แล้ว, THE
  SYSTEM SHALL ผ่านทั้ง suite รวม `SimCrossInstanceConsistencyTests` ทั้ง 2 test
- 9.4 WHEN รัน `bash docker/entrypoint.test.sh` และ `bash docker/migrate-entrypoint.test.sh`, THE
  SYSTEM SHALL เขียวทั้งคู่ (`fail=0`)
- 9.5 WHEN รัน `scripts/spec-trace.sh external-sim-separate-containers`, THE SYSTEM SHALL พิมพ์บรรทัด
  ขึ้นต้นด้วย `OK:`
- 9.6 WHEN grep คำว่า `02-external-sim` ทั้ง repo, THE SYSTEM SHALL เหลือศูนย์ hit นอกไฟล์ประวัติศาสตร์
  (`.ai/specs/*` ของ spec เก่าที่ปิดแล้ว, `retrospectives/`, `.pipeline/`)

## นอกขอบเขต (จงใจ)

- **cutover ไป upstream จริง (motordb/centerdb)** — งานนี้แค่แยก container ของตัวจำลอง ไม่เปลี่ยนว่า
  sim ยังคงเป็น sim
- **แก้ spec เก่า `products-sp-gateway`** — supersede ผ่าน spec นี้ + comment ในโค้ดเท่านั้น (REQ-4.3)
- **เพิ่ม SQL container ใน `docker-compose.prod.yml`** — DB tier ของ prod เป็น host แยกเสมอ (REQ-6.1)
- **เปลี่ยน seed data / row count / roster** — pin โดย `external-sim-shared-agent-network` tests
  (REQ-5)
- **แตะ `.env`/`.env.integration` จริง** — gitignored, operator แก้เองตาม runbook ที่อัปเดตใน REQ-8
