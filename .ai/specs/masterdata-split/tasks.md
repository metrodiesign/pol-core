# Implementation Tasks: masterdata-split

> Status: approved 2026-07-19

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass (it may touch many files). Decompose into sub-steps yourself at
> execution time — do NOT pre-split tasks here.

- [x] 1. Scaffold 4 โมดูล inert + test projects — สร้าง `src/Modules/{Divisions,Levels,Offices,Positions}/` (โมดูลละ 3 project ตาม template: entity standalone ยก logic จาก `MasterDataItem` คำต่อคำ, `XItem`+`IXStore`, no-op registration anchor, design-time config พหูพจน์ mirror facet ครบ 6 ตัว) + `tests/{Divisions,Levels,Offices,Positions}.Tests` (ย้าย domain-invariant tests จาก `MasterDataAndProfileTests.cs:19-50` แตกตามโมดูล) + ลงทะเบียนทั้งหมดใน `pol-core.slnx`; ยังไม่แตะ `HostModuleAssemblies`/consumer ใด — ของเก่า/ใหม่ compile คู่กัน "done" = solution build ผ่าน + test project ใหม่ 4 ตัวเขียว
     Satisfies: 1.1, 1.2, 1.6, 2.1, 2.2, 3.1, 3.2, 6.4, 7.1, 8.1. Verify: `dotnet build pol-core.slnx -warnaserror` + `dotnet test tests/Divisions.Tests tests/Levels.Tests tests/Offices.Tests tests/Positions.Tests`.
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 68 projects, 0 errors, 0 warnings; `dotnet test tests/<X>.Tests` ทีละตัว (dotnet test รับทีละ project) -> Divisions 6/6, Levels 6/6, Offices 6/6, Positions 6/6 passed
       - viewports: n/a — logic-only
       - deviations: none — 36 ไฟล์ generate จาก template เดิมด้วย script (gen-modules.py ใน scratchpad), slnx เพิ่ม 4 module folder + 4 test project ตำแหน่ง alphabetical; MasterData เดิมยังอยู่ครบ (inert coexistence ตามแผน)

- [x] 2. Atomic cutover ทุก consumer + migration surgery — สลับทั้งระบบจาก MasterData types ไป 4 โมดูลใหม่ใน slice เดียว (ModelDisjointness เทียบ CLR type — ห้ามมี state กลางทาง): Persistence.ControlPlane (DbContext, 4 store + 4 runtime config เอกพจน์, `ProfileLookup`, DI, csproj), Admins (`IProfileLookup`/`ProfileRef`/`ProfileValidation` แทน port เดิม + consumer 3 ไฟล์ + csproj 2 ชั้น), Hosts/Api (`MapMasterCrud<TStore,TItem>` + call sites, `MasterRefToWire` param type, `WriteAuthorizers` usings, `DesignTimeDbContextFactories` 1->4, csproj), tests (`Admins.Tests` fake enum-keyed + csproj, `WriteFloorTests:169`, assembly list 3 ไฟล์, `TransactionInventoryTests` 1 path -> 4, comment sweep), designer/snapshot surgery ตาม procedure ใน design.md (TempSplitCheck ว่าง = machine proof, ลบด้วยมือ, transplant per-file 13->0); กวาด `<see cref=` ค้าง "done" = ทุก gate unit-tier เขียวบน model ใหม่
     Satisfies: 1.5, 3.3, 3.4, 3.5, 3.6, 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 6.1, 6.2, 6.3, 6.5, 6.6, 7.2, 7.3, 7.4, 7.7, 8.2, 8.3, 8.4, 8.5. Depends on: 1. Verify: `dotnet build pol-core.slnx -warnaserror` + `dotnet test pol-core.slnx --filter "Category!=Integration"` + `scripts/check-migration-lineage.sh` (+ evidence: TempSplitCheck `Up()/Down()` ว่าง).
     Evidence:
       - test: `dotnet build pol-core.slnx -warnaserror` -> 68 projects, 0 errors, 0 warnings; `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0 ทุก suite (Architecture 201/201, Hosts 251/251, Admins 95/95, Merchants 114/114, Payments 59/59, ...); `dotnet test tests/Hosts.Tests --filter ModelConsistencyTests` -> 1/1; `scripts/check-migration-lineage.sh` -> "all 4 existing migration IDs discoverable"
       - machine proof (7.4): `dotnet ef migrations add TempSplitCheck` -> `Up()`/`Down()` ว่างเปล่าทั้งคู่ -> ลบไฟล์ probe ด้วยมือ เก็บ snapshot ที่ regen; designer surgery ผ่าน script find-and-assert: ทั้ง 4 ไฟล์ 18 -> 0 occurrences (base block ลบ, 4 standalone block transplant ต่อไฟล์, HasOne strings แก้)
       - viewports: n/a — logic-only
       - deviations: (1) ModelConsistencyTests เขียวตั้งแต่ก่อนแก้ snapshot — ยืนยันว่า relational diff ว่างจริงตาม design (แก้ designer/snapshot เพื่อ REQ-7.3 + hygiene); (2) occurrence จริงต่อ designer = 18 ไม่ใช่ ~13 ที่ design ประมาณไว้ — assert ใช้ค่าจริงและจบที่ 0; (3) reword comment เก่าที่เอ่ยชื่อ type ที่ retire (ProfileValidationTests, RawConnectionTests, ModelDisjointnessTests, PermissionGateSitesTests) กัน zero-grep gate ของ task 3

- [x] 3. ลบ MasterData + boundary guards + retired tokens + docs + DB evidence — `git rm -r src/Modules/MasterData/`; แทน `MasterDataArchitectureTests.cs` ด้วย Theory ไฟล์เดียว fail-closed ครอบ 4 โมดูล (Domain ปลอด EF/Infra, ไม่ ref กันเอง/Admins, Admins.Domain+Application ปลอด 12 assembly, Admins.Infrastructure ref แค่ 4 Domain); เพิ่ม 6 retired tokens ใน `scripts/check_rename_identifiers.py`; อัปเดต canon 5 ไฟล์ (`ARCHITECTURE.md` dated bullet, `platform-modules.md` row 15, `stack/dotnet.md` generic-store bullet, `SchemaNames.Cfg` XML doc, transaction inventory ใน rls design.md); รัน fresh-DB evidence pass + integration suite "done" = zero-grep `MasterData` ใน src/tests + ทุก gate เขียว
     Satisfies: 1.3, 1.4, 2.3, 2.4, 5.1, 5.2, 5.3, 5.4, 7.5, 7.6, 8.6, 9.1, 9.2, 9.3, 9.4, 9.5. Depends on: 2. Verify: `dotnet test pol-core.slnx --filter "Category!=Integration"` + `scripts/check-rename-identifiers.sh` + `grep -rn "MasterData" src/ tests/` = ว่าง + `docker compose down -v` -> migrate -> `assert-fresh-db.sql` + integration suite + `scripts/spec-trace.sh masterdata-split`.
     Evidence:
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> exit 0, 15 suite เขียวหมด (Architecture 214/214 รวม `RefModulesArchitectureTests` ใหม่, Hosts 251/251, Admins 95/95, 4 module suites 6/6 ละตัว; Products.Tests = 0 test มาแต่เดิม ไม่เกี่ยวงานนี้); `scripts/check-rename-identifiers.sh` -> OK หลังเพิ่ม 6 tokens; `git grep "MasterData" -- src tests` -> ว่าง
       - DB: `docker compose down -v` -> up (healthy) -> `dotnet ef database update` apply 4 migrations จากศูนย์ -> `scripts/check-migration-lineage.sh` OK -> `assert-fresh-db.sql` OK (schemas + master-data seed counts + pol_app grants, ไฟล์ assert ไม่แตะ) -> integration suite 41/41
       - spec-trace: 50 เกณฑ์อ้างครบ, EARS lint ผ่าน
       - viewports: n/a — logic-only
       - deviations: none — canon 5 ไฟล์อัปเดตครบ (ARCHITECTURE.md dated bullet, platform-modules row 15, stack/dotnet.md bullet, SchemaNames.Cfg XML doc, rls design.md inventory rows 14-15)

## Suggested execution batches

> Feature นี้ COUPLED ทั้งสาย (task 2 rewire สิ่งที่ task 1 สร้าง, task 3 ลบสิ่งที่ task 2
> ปลด reference) — default: รันทั้งหมดใน session เดียว `scripts/pane-loop.sh
> masterdata-split all-in-one` (หรือ `/spec-implement all`). ไม่มี Batch tag — 3 task
> ใหญ่คนละชนิด ไม่เข้าเกณฑ์ batch.
