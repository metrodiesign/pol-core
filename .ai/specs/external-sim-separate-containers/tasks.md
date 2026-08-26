# Implementation Tasks: external-sim-separate-containers

> Status: unknown

> แต่ละ task เป็นชิ้นที่ verify แยกได้ อ่าน `design.md` ก่อนเริ่มทุก task — การตัดสินใจเรื่อง SQL split,
> `IntegrationDb.SimServer` routing, per-server probe counter, `.env*` git blob swap ถูกล็อกไว้แล้ว
> ห้าม re-derive

> Superseded (บางส่วน): **ทุกจุดในไฟล์นี้ที่ระบุว่า `02-hippo-sim.sql`/`03-mammoth-sim.sql` สร้าง/ใช้ login
> `pol_app`** ถูกแทนที่โดย spec `sim-db-separate-logins` (`.pipeline/sim-db-separate-logins/spec.md`,
> 2026-08-05) — ไฟล์ 02 สร้าง `hippo_app` (sqlcmd variable `HIPPO_APP_PASSWORD`), ไฟล์ 03 สร้าง
> `mammoth_app` (`MAMMOTH_APP_PASSWORD`) คนละ password กัน และทั้งสองไฟล์ลบ `pol_app` เดิมออกจาก sim
> instance เองแบบ idempotent — spec นี้ปิดแล้ว task log ด้านล่างเป็นบันทึกของสิ่งที่ทำ ณ ตอนนั้น
> ไม่แก้ย้อนหลัง

- [x] 1. SQL split + local compose wiring — `docker/bootstrap/02-hippo-sim.sql`/`03-mammoth-sim.sql`
  ใหม่ (mechanical split จาก `02-external-sim.sql`, seed byte-identical, prefix ข้อความ self-check
  เปลี่ยน, `03-mammoth-sim.sql` มี `CREATE LOGIN pol_app` ของตัวเอง + ตัด cross-database check 2 บล็อก)
  + ลบ `docker/bootstrap/02-external-sim.sql` + `docker-compose.yml` เพิ่ม service `hippo-db`/
  `mammoth-db` + `pol-db-init` entrypoint chain ใหม่ 3 คำสั่ง
     Satisfies: REQ-1 (ทั้งหมด), REQ-2 (ทั้งหมด), REQ-3.1
     Depends on: — (จุดเริ่ม)
     Verify: `docker compose down -v && docker compose up -d && docker compose ps -a` ทุก service
     `healthy`/`Exited (0)`; `docker compose logs pol-db-init` เห็น `02-hippo-sim: hippodb OK (200
     documents, 42 ...)` + `03-mammoth-sim: mammothdb OK (200 documents, 40 ...)`; diff seed block
     กับต้นฉบับ (`git show HEAD:docker/bootstrap/02-external-sim.sql`) ยืนยัน byte-identical; รัน
     `docker compose up -d` ซ้ำ (idempotent, AC-2)
     Evidence:
       - AC-1/AC-2 (`tests.md`): `docker compose down -v && docker compose up -d` -> `pol-db-init`
         exited 0, sqlcmd `COUNT(*)` hippodb(:11434)=200 mammothdb(:11435)=200; รัน `up -d` ซ้ำบน
         stack ที่ up อยู่แล้ว -> exited 0 อีกครั้ง ยัง 200/200 (ไม่ duplicate — idempotent guard
         ทำงานจริง)
       - seed byte-identical (`audit.md`): diff กับ `git show HEAD:docker/bootstrap/02-external-sim.sql`
         ตรงกันทุก byte ยกเว้น prefix ข้อความ self-check + cross-db check 2 บล็อกที่ตัดตามแผน (ย้ายไป
         task 3); `CREATE LOGIN pol_app` ครบทั้ง 2 ไฟล์ใหม่ วางก่อน `USE` ถูกต้อง, caller ส่ง
         `-v POL_APP_PASSWORD` ครบ 8 จุด
       - deviations: ไม่มี

- [x] 2. `.NET` config — ลบ `PostConfigure<SpDocumentOptions>` derive fallback ใน
  `src/Hosts/Api/Program.cs` + comment ใหม่อ้าง spec นี้ supersede REQ-3.4 เดิม + แก้ XML doc ของ
  `SpDocumentOptions.cs` (ตัดเรื่อง derive, คง REQ-5.7 rationale)
     Satisfies: REQ-4 (ทั้งหมด)
     Depends on: — (อิสระจาก task 1)
     Verify: `dotnet build pol-core.slnx -warnaserror`; อ่านโค้ดยืนยันไม่มี fallback เหลือ; host boot
     ได้โดยไม่ตั้ง `SpDocument:*` (Hosts.Tests เขียว, ไม่มี `.ValidateOnStart()`)
     Evidence:
       - AC-3 (`tests.md`, mixed live+unit): live boot ไม่ตั้ง `SpDocument__*` -> `Now listening`/
         `Application started`, `/health/live` -> 200, `/api/v1/products` (ไม่มี auth) -> 401 (route
         matched ไม่ใช่ 404); unit `SpDocumentGatewayConfigTests`/`ProblemDetailsExceptionHandlerTests`
         ยืนยัน `UpstreamUnavailableException` -> 503
       - `dotnet build pol-core.slnx -warnaserror` -> 64 projects 0 errors/0 warnings; `Hosts.Tests`
         458/458 (17 host boot จริงไม่ตั้ง `SpDocument:*`, ไม่มี `.ValidateOnStart()`)
       - deviations: ไม่มี

- [x] 3. Integration test routing + cross-instance test ใหม่ — `tests/Integration.Tests/IntegrationDb.cs`
  เพิ่ม `SimServer(catalog)`/`SaForCatalog(catalog)`, `ForCatalog` route ผ่าน `SimServer` (signature
  เดิม, call site 3 จุดไม่แตะ) + `tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs` ใหม่
  (DocumentNo disjoint + SaleCode roster identity null-safe)
     Satisfies: REQ-3.2, REQ-3.3, REQ-3.4, REQ-5 (ทั้งหมด)
     Depends on: task 1 (ต้องมี hippo-db/mammoth-db ขึ้นจริงถึง verify ได้), task 2 (SpDocumentOptions
     ต้อง config ผ่าน env ไม่ใช่ derive ก่อน adapter integration test จะเขียว)
     Verify: `source .env.integration && dotnet test tests/Integration.Tests --filter
     "Category=Integration"` เขียวทั้ง suite รวม `SimCrossInstanceConsistencyTests` 2 test ใหม่ และ
     `SpDocumentContractTests`/`SpDocumentGatewayIntegrationTests` เดิม (routing ใหม่ไม่ทำพัง)
     Evidence:
       - AC-4 (`tests.md`): `dotnet test --filter "Category=Integration"` -> 130/130 ผ่าน รวม
         `SpDocumentContractTests` 60/60 (routing ผ่าน `SimServer` ไม่ทำพัง call site เดิม)
       - AC-5 (`tests.md`): `SimCrossInstanceConsistencyTests` 2/2
         (`No_DocumentNo_is_shared_between_hippodb_and_mammothdb`,
         `An_agent_present_on_both_sides_has_the_same_identity_on_both_sides`) พร้อม sanity assert
         200/200 rows + overlap=6 กันผลลัพธ์ vacuous
       - `ForCatalog` คง signature เดิม, call site เดิม 3 จุดไม่ถูกแตะ (`audit.md` หัวข้อ
         "integration routing")
       - deviations: ไม่มี

- [x] 4. Prod plumbing — `docker-compose.prod.yml` env 4 ตัวใหม่ที่ `migrate`+`api` +
  `docker/entrypoint.sh` refactor `build_conn` helper + ประกอบ `SpDocument__*` conditional +
  `docker/migrate-entrypoint.sh` refactor `wait_for_db` helper (คง TLS classification เดิม) เรียก 3
  รอบ + bootstrap 2 ไฟล์ใหม่ต่อ server + `docker/migrate-entrypoint.test.sh` env ใหม่ + sqlcmd stub
  per-`-S` probe counter + assert ไฟล์ใหม่ + `.env.prod.example` section sim DB tiers (git blob swap)
     Satisfies: REQ-6 (ทั้งหมด)
     Depends on: task 1 (ชื่อไฟล์ bootstrap ใหม่ต้องมีอยู่ก่อน migrate-entrypoint.sh อ้างถึง)
     Verify: `bash docker/entrypoint.test.sh` + `bash docker/migrate-entrypoint.test.sh` เขียวทั้งคู่
     (`fail=0`); `docker compose -f docker-compose.prod.yml config -q` fail ด้วย `:?` เมื่อไม่ตั้ง
     `HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER`, ผ่านเมื่อ export placeholder ครบ (AC-6)
     Evidence:
       - AC-6 (`tests.md`): `docker compose -f docker-compose.prod.yml config -q` -> exit 0 เมื่อ env
         ครบ; exit 1 พร้อม `required variable HIPPO_DB_SERVER is missing a value` เมื่อขาด (fail-fast
         ตาม `:?`)
       - AC-7 — เลขล่าสุดหลัง rework #3 (`changes.md` section "รอบแก้ #3", ไม่ใช่ 17/13 ของรอบแรกใน
         `tests.md`): `bash docker/entrypoint.test.sh` -> `pass=21 fail=0`; `bash
         docker/migrate-entrypoint.test.sh` -> `pass=43 fail=0`
       - `build_conn`/`wait_for_db` คง TLS-classification/CA-pin logic เดิมครบ ไม่ปรับปรุงข้างเคียง
         (`audit.md` หัวข้อ "Refactor 2 ตัวคุมขอบเขตดี")
       - deviations: BLOCKING รอบ audit แรก (`build_conn` ใช้ `echo` บน dash ทำ password/TLS clause
         เพี้ยน) แก้เป็น `printf '%s\n'` ใน rework 1/5; `entrypoint.test.sh` stub ขยายครอบ
         `SpDocument__*` 3 ค่าใน rework 2/5 ตาม must-fix ของ review (mutation red-green proof ยืนยัน
         แล้ว) — รายละเอียดเต็มใน `changes.md` ("รอบแก้ #2"/"รอบแก้ #3") และ `review.md` (รอบตัดสิน
         สุดท้าย)

- [x] 5. CI ทั้งสอง — `.github/workflows/ci.yml` เพิ่ม service `hippo`/`mammoth` + env + bootstrap
  step 2 คำสั่ง + docker-build render-check placeholder; `.gitlab-ci.yml` mirror ทั้งหมด (service
  alias, variables, wait-loop 3 servers, bootstrap 2 คำสั่ง, render-check export block)
     Satisfies: REQ-7 (ทั้งหมด)
     Depends on: task 1 (ไฟล์ bootstrap ใหม่), task 4 (env var name ที่ compose prod ต้องการตรงกับที่
     render-check ใน CI ใช้)
     Verify: อ่าน workflow YAML ยืนยัน service/env/step ครบ (รัน CI จริงอยู่นอกเครื่อง local — ตรวจ
     ด้วยการ diff กับ pattern ของ service `sql` เดิม); `docker compose -f docker-compose.prod.yml
     config -q` ด้วย placeholder เดียวกับที่ CI ใช้
     Evidence:
       - `.github/workflows/ci.yml`/`.gitlab-ci.yml` (`changes.md` T6): service `hippo`(11434)/
         `mammoth`(11435) mirror image/env/healthcheck จาก service `sql` เดิม, bootstrap step แยก 2
         คำสั่งต่อไฟล์ (`-v POL_APP_PASSWORD` ทั้งคู่), render-check env เพิ่ม `HIPPO_DB_SERVER`/
         `MAMMOTH_DB_SERVER` ครบทั้ง 2 CI (`audit.md` หัวข้อ "render-check placeholder ครบทั้ง 2 CI")
       - AC-6 (`tests.md`): `docker compose -f docker-compose.prod.yml config -q` ผ่านด้วย placeholder
         ชุดเดียวกับที่ CI render-check ใช้
       - verify ตามตัวอักษรรันไม่ได้ในเครื่อง local (CI จริงรันนอกเครื่อง) — ตรวจด้วยการอ่าน YAML
         เทียบ pattern ของ service `sql` เดิมแทนตามที่ task ระบุไว้เอง
       - deviations: ไม่มี

- [x] 6. Docs sweep — `docs/reference/products.md`, `docs/runbooks/local-dev-run.md`,
  `docs/runbooks/deploy-self-host.md`, `docs/reference/db-connection-and-rls.md` อัปเดตให้สะท้อน
  topology ใหม่
     Satisfies: REQ-8 (ทั้งหมด)
     Depends on: task 1, task 4 (เอกสารต้องสะท้อนโครงจริงหลัง implement เสร็จ)
     Verify: grep ตำแหน่งที่ spec ระบุ (container เดียว, derive connection string, `:11434` orphan
     note) ไม่มีข้อความเก่าเหลือ
     Evidence:
       - 4 ไฟล์ตาม `changes.md` T7: `docs/reference/products.md` (3 จุด read-path/สรุปสถานะ/ตาราง
         column), `docs/runbooks/local-dev-run.md` (§2.2 ยก 5 service, §3 topology table +2 แถว, §6
         env 6 ตัว), `docs/runbooks/deploy-self-host.md` (§4.0 อ้างไฟล์ bootstrap ใหม่ 2 ไฟล์ + DROP
         แยก 3 คำสั่งต่อ server), `docs/reference/db-connection-and-rls.md` (Flow A §9)
       - เก็บ nits เพิ่มหลัง review (docs-only follow-up ไม่ block ตาม `review.md` section "ก่อน
         ship"): `local-dev-run.md:486` (`:11434`/`:11435` เป็น sim DB ของ compose แล้ว ไม่ใช่ orphan
         รุ่น rf1 อีกต่อไป), `:66-67` (`pol-db-init` chain 3 คำสั่งข้าม 3 instance), `:35-40`
         (checklist `.env` เพิ่ม `SpDocument__MotorConnectionString`/`__NonMotorConnectionString`) —
         ปิดครบในรอบ commit-hygiene นี้
       - deviations: ไม่มี

- [x] 7. Definition of Done gate — spec artifacts ตัวเองต้อง trace ครบ + regression suite เขียวทั้งชุด
  + zero-reference cleanup
     Satisfies: REQ-9 (ทั้งหมด)
     Depends on: task 1-6 ทั้งหมด
     Verify: `scripts/spec-trace.sh external-sim-separate-containers` พิมพ์ `OK:`; `dotnet build
     pol-core.slnx -warnaserror`; `dotnet test pol-core.slnx --filter "Category!=Integration"`;
     `dotnet test --filter "Category=Integration"`; `bash docker/entrypoint.test.sh`; `bash
     docker/migrate-entrypoint.test.sh`; `grep -rn "02-external-sim" .` เหลือเฉพาะใน
     `.ai/specs/*` ของ spec เก่า, `retrospectives/`, `.pipeline/`
     Evidence:
       - AC-8 (`tests.md`): `bash scripts/spec-trace.sh external-sim-separate-containers` -> `OK:
         เกณฑ์ 45 ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ`
       - AC-9 (`tests.md`): `grep -rln "02-external-sim" .` กรอง `.git/`/`retrospectives/`/
         `.pipeline/`/`.ai/specs/` ออก -> ว่างสนิท (ไม่มี hit ในโค้ด/สคริปต์/docs/CI)
       - AC-10 (`tests.md`): `dotnet build pol-core.slnx --no-restore -warnaserror` -> 64 projects 0
         errors/0 warnings; `dotnet test --no-build --filter "Category!=Integration"` -> 1642/1642
         (Hosts.Tests 458/458, Architecture.Tests 206/206)
       - `audit.md`: PASS 0 BLOCKING (BLOCKING เดียวรอบแรกปิดแล้ว ยืนยันซ้ำ 2 รอบ); `review.md`:
         APPROVE WITH NITS (must-fix เดียวปิดจริงด้วย mutation red/green proof 4 แบบอิสระ)
       - deviations: ไม่มี
