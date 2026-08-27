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
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 2. `.NET` config — ลบ `PostConfigure<SpDocumentOptions>` derive fallback ใน
  `src/Hosts/Api/Program.cs` + comment ใหม่อ้าง spec นี้ supersede REQ-3.4 เดิม + แก้ XML doc ของ
  `SpDocumentOptions.cs` (ตัดเรื่อง derive, คง REQ-5.7 rationale)
     Satisfies: REQ-4 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 3. Integration test routing + cross-instance test ใหม่ — `tests/Integration.Tests/IntegrationDb.cs`
  เพิ่ม `SimServer(catalog)`/`SaForCatalog(catalog)`, `ForCatalog` route ผ่าน `SimServer` (signature
  เดิม, call site 3 จุดไม่แตะ) + `tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs` ใหม่
  (DocumentNo disjoint + SaleCode roster identity null-safe)
     Satisfies: REQ-3.2, REQ-3.3, REQ-3.4, REQ-5 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 4. Prod plumbing — `docker-compose.prod.yml` env 4 ตัวใหม่ที่ `migrate`+`api` +
  `docker/entrypoint.sh` refactor `build_conn` helper + ประกอบ `SpDocument__*` conditional +
  `docker/migrate-entrypoint.sh` refactor `wait_for_db` helper (คง TLS classification เดิม) เรียก 3
  รอบ + bootstrap 2 ไฟล์ใหม่ต่อ server + `docker/migrate-entrypoint.test.sh` env ใหม่ + sqlcmd stub
  per-`-S` probe counter + assert ไฟล์ใหม่ + `.env.prod.example` section sim DB tiers (git blob swap)
     Satisfies: REQ-6 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 5. CI ทั้งสอง — `.github/workflows/ci.yml` เพิ่ม service `hippo`/`mammoth` + env + bootstrap
  step 2 คำสั่ง + docker-build render-check placeholder; `.gitlab-ci.yml` mirror ทั้งหมด (service
  alias, variables, wait-loop 3 servers, bootstrap 2 คำสั่ง, render-check export block)
     Satisfies: REQ-7 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 6. Docs sweep — `docs/reference/products.md`, `docs/runbooks/local-dev-run.md`,
  `docs/runbooks/deploy-self-host.md`, `docs/reference/db-connection-and-rls.md` อัปเดตให้สะท้อน
  topology ใหม่
     Satisfies: REQ-8 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
- [x] 7. Definition of Done gate — spec artifacts ตัวเองต้อง trace ครบ + regression suite เขียวทั้งชุด
  + zero-reference cleanup
     Satisfies: REQ-9 (ทั้งหมด)
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
