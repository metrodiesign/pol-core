# Implementation Tasks: One-Based Persisted Enum Storage

> Status: approved 2026-08-08

> แต่ละ task เป็น slice ที่แก้และตรวจสอบได้ครบในรอบเดียว โดยคง historical migrations และเปลี่ยนเฉพาะ current runtime/database contract

- [x] 1. เปลี่ยน persisted domain enum และ merchant registration contract — กำหนดค่าตัวเลข one-based ครบทุก enum ที่อยู่ใน scope, ทำ `IdentityType` ของ registration/profile เป็น required, เพิ่ม validation ที่ API/domain และปรับ unit tests ที่เกี่ยวข้อง
     Satisfies: REQ-1 (all criteria), REQ-3.3-REQ-3.4. Verify: `dotnet test tests/Merchants.Tests/Merchants.Tests.csproj --no-restore` และ `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore`
    - deviations: full `Hosts.Tests` run stalled after build and was cancelled; focused registration-form suite passed
- [x] 2. ทำ runtime persistence และ SQL consumers ให้ตรง contract ใหม่ — อัปเดต EF configurations ทั้ง migration-owner/runtime contexts, required columns, IAM role check, payment open-session filter, seed/runtime queries และ raw SQL/test literals โดยไม่แตะ non-target enums
     Satisfies: REQ-2 (all criteria), REQ-3.1-REQ-3.2. Depends on: 1. Verify: `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore` และ `rg -n "(Scope|Status|Psp|IdentityType) IN? \(0|= 0|= 1\)" src tests`
       - deviations: live SQL integration suite deferred to task 3; current source/test SQL consumers were updated and focused model contracts passed
- [x] 3. เพิ่ม forward/reverse migration แบบ fail-safe — สร้าง migration ใหม่และ model snapshot/designer, preflight `NULL`/legacy invalid values ก่อน data/schema change, แปลงค่าทุก field แบบ explicit, อัปเดต nullability/check/index และเพิ่ม migration integration tests รวม rollback
     Satisfies: REQ-4 (all criteria). Depends on: 2. Verify: `dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-restore` และ migration shape/preflight tests
       - deviations: existing local `VCentralPay` contained mixed legacy/current values from earlier test runs; the migration refused before mutation as designed. Live SQL evidence ran against a fresh scratch database after applying the complete migration chain.
- [x] 4. อัปเดต current Entity and Field Reference และ verification — แก้ mappings, SQL type/nullability, master-data references, filtered-index/migration-chain docs และเพิ่ม parity checks ให้ docs, model และ migration แสดง contract เดียวกัน
     Satisfies: REQ-5 (all criteria). Depends on: 1, 2, 3. Verify: `scripts/spec-trace.sh enum-one-based-storage`, `git diff --check` และ docs/reference parity checks
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
