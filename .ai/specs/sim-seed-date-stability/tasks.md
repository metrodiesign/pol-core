# Implementation Tasks: ทำให้วันที่ของ sim seed เสถียร

> Status: approved 2026-08-06

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. anchor marker ในไฟล์ seed ทั้งสอง — เพิ่ม `dbo.SeedInfo` (แถวเดียว, `AnchorDate` จาก `@today`
     ก้อนเดียวกับข้อมูล), `GRANT SELECT` ให้ `hippo_app`/`mammoth_app`, comment ประกาศหน่วย UTC หัวไฟล์
     ทั้ง `02-hippo-sim.sql` และ `03-mammoth-sim.sql` — done = replay ผ่าน self-check เดิมครบและ
     `AnchorDate` = วันนี้ของ sim
     Satisfies: REQ-2.1, REQ-2.2, REQ-2.4. Verify: `sqlcmd -b -i docker/bootstrap/02-hippo-sim.sql`
     (และ `03`) ต่อ sim instance local แล้ว `SELECT AnchorDate FROM dbo.SeedInfo` เท่ากับ
     `CAST(GETDATE() AS date)` ของ instance นั้น
     Evidence:
       - test: `sqlcmd -S localhost,11434 -U sa -N -C -b -v HIPPO_APP_PASSWORD=*** -i docker/bootstrap/02-hippo-sim.sql` -> `02-hippo-sim: hippodb OK (200 documents, 42 in the default search window).`; `03-mammoth-sim.sql` บน `localhost,11435` -> `mammothdb OK (200 documents, 40 ...)`
       - test: `SELECT AnchorDate, CAST(GETDATE() AS date) FROM dbo.SeedInfo` -> 2026-08-06 = 2026-08-06 MATCH ทั้งสองฝั่ง; SELECT ซ้ำผ่าน principal `hippo_app`/`mammoth_app` สำเร็จ (พิสูจน์ GRANT)
       - viewports: n/a — logic-only
       - deviations: none

- [x] 2. `SimSeedFixture` + collection + ย้าย test มาใช้ anchor — hoist `SqlScripts` (แตก batch/หา repo
     root จาก `SeedDemoIntegrationTests`), สร้าง fixture (ตรวจ stale, replay, verify, `Anchor`,
     `GuardAnchorAsync`) + `SimSeedCollection`, ให้ 5 class ที่แตะ sim เข้า collection, แทน
     `DateTime.Today` ใน `SpDocumentContractTests.cs:465,571-572` ด้วย `Anchor`, เรียก guard ใน
     `SearchAsync` helper, แก้ comment `:19` — done = suite เขียวโดย assertion ค่าเป๊ะเดิมทุกตัวไม่ถูกแตะ
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-2.3, REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.4,
     REQ-3.5, REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4, REQ-5.1, REQ-5.2, REQ-5.3, REQ-5.5.
     Depends on: 1. Verify: `dotnet test` (Integration filter) เขียวครบ;
     `grep -rn "DateTime.Today" tests/Integration.Tests` = 0 จุด; diff ของ test ที่ไม่พึ่งวันที่มีแค่
     attribute `[Collection]`
     Evidence:
       - test: `dotnet test pol-core.slnx --filter "Category=Integration"` -> Integration.Tests 150 passed / 0 failed; Hosts.Tests 1 passed / 0 failed (`MerchantCatalogueLiveEndpointTests`)
       - test: `grep -rn "DateTime.Today" tests/Integration.Tests` -> 0 จุด (แม้ใน comment)
       - test: `git diff --stat` -> `DocumentNoCollationIntegrationTests`/`SimCrossInstanceConsistencyTests`/`SpDocumentGatewayIntegrationTests` +1 บรรทัด (attribute `[Collection]`) ต่อไฟล์; assertion 42/40 + landmark ทุกตัวไม่ถูกแตะ
       - viewports: n/a — logic-only
       - deviations: `SearchAsync`/`AllPagesAsync`/`RejectionAsync` เปลี่ยน static -> instance method เพื่อถือ fixture (จำเป็นต่อการเรียก `GuardAnchorAsync` ใน helper เดียว — design ระบุ guard ที่ SearchAsync อยู่แล้ว); `IntegrationDb.Require` เปลี่ยน private -> internal ให้ fixture ใช้ข้อความ error เดิมตอน env หาย

- [x] 3. หลักฐาน mutation + เอกสาร — รันตาราง Testing Strategy ใน design.md ให้ครบ (จำลอง container
     เก่าต้องเขียวเอง, แก้ window SP เป็น -5 เดือนต้องแดง, guard stale ต้องพูดค่าทั้งสอง, รันด้วย
     `TZ=Asia/Bangkok` ผลเท่า UTC) บันทึกผลเป็น Evidence, อัปเดต `docs/runbooks/local-dev-run.md`
     (ไม่ต้อง `down -v` เพื่อแก้วันที่อีก) — done = evidence ครบทุกแถว + runbook ตรงพฤติกรรมใหม่
     Satisfies: REQ-4.5, REQ-5.4, REQ-5.6. Depends on: 2. Verify: evidence แนบใต้ task นี้ +
     CI job `dotnet integration (live SQL 2025)` เขียวบน PR
     Evidence:
       - test (container เก่า, REQ-1.1/1.3): `UPDATE SeedInfo SET AnchorDate -= 1 day` + เลื่อน `StartDate`/`EndDate`/`PaidDate` ทุกแถวถอย 1 วันทั้งสอง sim -> `dotnet test` (Integration filter) -> 150 passed / 0 failed โดยไม่มีขั้นตอนมือ; `SELECT AnchorDate` หลังรัน = 2026-08-06 (fixture ซ่อมเอง)
       - test (mutation, REQ-4.5): แก้ `02-hippo-sim.sql:249` เป็น `DATEADD(month, -5, @today)` + ทำ anchor stale ให้ fixture replay ไฟล์ mutate -> `Failed: 10, Passed: 140` (window/TotalRows tests แดงจริง) -> revert + replay สะอาด -> 150 passed กลับ
       - test (guard, REQ-1.4): `UPDATE SeedInfo SET AnchorDate -= 1 day` โดยไม่แตะข้อมูล แล้วเรียก `GuardAnchorAsync` ตรง ๆ (temp test, ลบแล้ว) -> `InvalidOperationException: Sim seed anchor 2026-08-05 no longer matches the sim's own today 2026-08-06 — ...` (มีค่าทั้งสองตัว, `Assert.Contains` ทั้งคู่ผ่าน)
       - test (timezone, REQ-1.2/2.2/2.3): `TZ=Asia/Bangkok dotnet test` (Integration filter) -> 150 passed / 0 failed เท่ารัน UTC
       - test (gate เต็ม): unit ทั้ง solution (`Category!=Integration`) -> 0 failure; Integration -> Hosts.Tests 1/1 + Integration.Tests 150/150; `scripts/spec-trace.sh sim-seed-date-stability` -> OK เกณฑ์ 24 ข้อครบ
       - viewports: n/a — logic-only
       - deviations: CI job `dotnet integration (live SQL 2025)` บน PR ยังไม่รัน — จะเขียวได้ต่อเมื่อเปิด PR (ขั้น ship ถัดไป); sqlcmd ที่ใช้ UPDATE ตรง ๆ ต้อง `SET QUOTED_IDENTIFIER ON` ก่อน (filtered index บน dbo.Documents — trap เดิมของ repo)

## Suggested execution batches

> DEFAULT for a COUPLED feature (tasks share primitives/data/lib): run ALL tasks in
> ONE session — `scripts/pane-loop.sh sim-seed-date-stability all-in-one` (or `/spec-implement all`).
> Separate sessions do NOT share cache, so each one re-pays the cold cache-write to
> re-acquire shared context. Split only for accuracy isolation.

ทั้ง 3 task พึ่งกันเป็นสายเดียว (marker -> fixture -> evidence) — all-in-one session เดียวเหมาะสุด
ไม่มี Batch tag แยก
