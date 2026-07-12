# Requirements — masterdata-module

> Status: approved 2026-07-13 (quick, no gates)

## Context

`Position` / `Office` / `Level` / `Division` (ตำแหน่ง / สถานที่ปฏิบัติงาน / ระดับ / ฝ่าย-ภาค) เป็น
reference data ของโปรไฟล์พนักงาน ที่วันนี้ถูกฝังอยู่ใต้ module `Admins`
(`Admins.Domain.MasterData` / `Admins.Application.MasterData` /
`Admins.Infrastructure.Persistence.MasterData`, ตาราง `admin.Positions` ฯลฯ) — ทั้งที่มันไม่ใช่
sub-domain ของ admin identity: `User` แค่ **อ้างถึง** มันผ่าน FK 4 ตัวเท่านั้น (`PositionId`,
`OfficeId`, `LevelId`, `DivisionId`).

งานนี้ยก MasterData ออกมาเป็นโมดูลของตัวเองตาม shape เดียวกับ `Iam` (rf2) และจัดชื่อ
namespace/folder ให้ตามกฎ naming L1-L8. **Behaviour-preserving ทั้งหมด** — ไม่มี endpoint,
permission key, request/response contract ใดเปลี่ยน.

Locked decisions (user ตัดสิน 2026-07-13 — ห้าม re-litigate):
- namespace/folder ต่อ master = **พหูพจน์**, ชื่อ type = **เอกพจน์** (L3)
- ตารางย้ายไป **schema ใหม่** — เลือก `cfg` (อยู่ใน 9 schema ที่ v5 ล็อกไว้แล้ว; rf3 จะมาเติม
  payment config ใน schema เดียวกัน ไม่ต้องเปิด schema ที่ 10)
- route คงเดิม `/api/v1/admins/{positions|offices|levels|divisions}` — ไม่ใช่ contract change
- permission key คงเดิม (`iam.*` ของ rf2) — ไม่แตะ catalog

## Requirements

### REQ-1 — Module extraction

- **1.1** The system SHALL host Position, Office, Level, Division และ base type ของมัน ใน module
  ของตัวเองที่ `src/Modules/MasterData/` ประกอบด้วย 3 project: `MasterData.Domain`,
  `MasterData.Application`, `MasterData.Infrastructure`.
- **1.2** The system SHALL NOT leave any Position/Office/Level/Division type, EF configuration,
  หรือ store implementation ค้างอยู่ใน project ใดของ module `Admins`.
- **1.3** The 3 projects ใหม่ SHALL ถูกเพิ่มเข้า `pol-core.slnx` และ compile เป็นส่วนหนึ่งของ
  solution build.
- **1.4** `MasterData.Infrastructure` SHALL ถูกลงทะเบียนใน `ModuleAssemblies` ของทุก host ที่
  โหลด EF model (Api และ Worker) เพื่อให้ `PolDbContext` เก็บ entity configuration ของมันได้.

### REQ-2 — Naming (hierarchical-naming L1-L8)

- **2.1** Sub-namespace/folder ของแต่ละ master list SHALL เป็นพหูพจน์:
  `MasterData.Domain.Positions`, `.Offices`, `.Levels`, `.Divisions` (L3).
- **2.2** ชื่อ type SHALL คงเป็นเอกพจน์: `Position`, `Office`, `Level`, `Division` (L3) — ห้าม
  rename เป็นพหูพจน์.
- **2.3** Base type ที่ 4 aggregate ใช้ร่วมกัน SHALL อยู่ที่ module-root namespace
  (`MasterData.Domain`) และ SHALL คงชื่อ `MasterDataItem` — L4 หยุดตรงนี้เพราะชื่อที่สั้นลง
  (`Item`) กำกวมกับ `CartItem`/`OrderItem` ที่มีอยู่แล้ว.
- **2.4** ชื่อตาราง SHALL คงเป็น `Positions` / `Offices` / `Levels` / `Divisions` (พหูพจน์อยู่แล้ว
  ตาม L7) — เปลี่ยนเฉพาะ schema ที่ qualify มัน.

### REQ-3 — Schema

- **3.1** ตารางทั้ง 4 SHALL อยู่ใน schema `cfg` แทน `admin`.
- **3.2** `SchemaNames` SHALL ประกาศค่าคงที่ `Cfg = "cfg"` และ EF configuration ของ 4 entity
  SHALL อ้าง constant นั้น (ห้าม hardcode string ที่ call site).
- **3.3** Schema `cfg` SHALL ถูกสร้างแบบ `AUTHORIZATION dbo` เหมือนทุก schema อื่น (rf1 —
  ownership chaining).
- **3.4** WHEN a fresh DB ถูก migrate, THE system SHALL ให้สิทธิ์ `SELECT, INSERT, UPDATE` บน
  `cfg.Positions`/`Offices`/`Levels`/`Divisions` แก่ principal `pol_admin` เท่านั้น — `pol_app`
  SHALL ไม่ได้รับสิทธิ์ใดบน `cfg.*` (เท่ากับสิทธิ์เดิมบน `admin.*` ทุกประการ).
- **3.5** ตาราง `cfg.*` SHALL อยู่นอก RLS policy (control-plane reference data — เหมือน `iam.*`).
- **3.6** FK 4 ตัวบน `admin.PlatformUsers` SHALL ยังคงบังคับ referential integrity ข้าม schema
  ไปยัง `cfg.*` (cross-schema FK — precedent: `admin.RoleAssignments` -> `iam.Roles`).

### REQ-4 — Module boundary

- **4.1** `Admins.Domain` และ `Admins.Application` SHALL NOT อ้างถึง `MasterData.Application`
  หรือ `MasterData.Infrastructure` (published-language rule เดียวกับ Iam ใน rf2 — อ้างได้เฉพาะ
  `MasterData.Domain`).
- **4.2** `MasterData.Domain` และ `MasterData.Application` SHALL NOT อ้างถึง module `Admins`
  ชั้นใดเลย (MasterData ไม่รู้จักผู้ใช้ของมัน).
- **4.3** `MasterData.Domain` SHALL NOT อ้าง EF Core หรือ Infrastructure ชั้นใด.
- **4.4** Port ที่ `Admins` ใช้ตรวจ/แปลง FK โปรไฟล์ SHALL ถูกประกาศใน `Admins.Application`
  (port เป็นของผู้เรียก) และ implement ใน `Admins.Infrastructure` — ตรงตาม precedent rf2 ที่
  `Admins.Infrastructure` query `iam.Roles` ตรงโดยใช้ type ของ `Iam.Domain`.
- **4.5** Architecture.Tests SHALL assert REQ-4.1, 4.2 และ 4.3 เป็น test จริง (fail-closed —
  ผูก assembly name จริงเหมือน `Module_key_matches_its_real_assembly_names`).

### REQ-5 — Behaviour preservation

- **5.1** Endpoint ทั้ง 4 ชุด SHALL คง path เดิม `/api/v1/admins/{positions|offices|levels|divisions}`
  (+ `/{id:guid}` สำหรับ PUT), คง verb เดิม (GET list, POST create, PUT update) และคง
  request/response shape เดิม.
- **5.2** Endpoint เหล่านั้น SHALL คง permission key เดิมที่ gate อยู่วันนี้ — ห้ามเพิ่ม/แก้/ลบ
  key หรือ group ใดใน iam catalog.
- **5.3** Domain invariant เดิม SHALL ไม่เปลี่ยน: `Code` ตรง `^[a-z0-9_]+$`, immutable หลังสร้าง,
  unique ต่อตาราง (ซ้ำ -> 409); `Rename` แก้ได้แค่ `Name`; master ที่ inactive ยังถูกอ้างโดย
  แถวเดิมได้แต่ assign ใหม่ไม่ได้ (400).
- **5.4** WHEN a create/update-profile request อ้าง FK ที่ไม่มีอยู่จริงหรือ inactive, THE system
  SHALL ตอบ 400 เหมือนเดิม.
- **5.5** Seed data เดิม (HR master rows) SHALL ยังถูก seed ครบเท่าเดิม ด้วย GUID เดิม — เปลี่ยน
  เฉพาะ schema ปลายทาง.

### REQ-6 — Migrations (big-bang, pre-prod)

- **6.1** THE system SHALL แก้ migration 3 ไฟล์เดิมในที่ (`InitialSchema`, `SecurityObjects`,
  `SeedData`) ตาม precedent big-bang ของ rf1/rf2 — ห้ามเพิ่ม migration ใหม่สำหรับการย้าย schema.
- **6.2** WHEN `dotnet ef database update` รันบน DB เปล่า, THE system SHALL สร้างตารางทั้ง 4 ใน
  `cfg`, ตั้ง grant ตาม REQ-3.4 และ seed ตาม REQ-5.5 โดยไม่มี error.
- **6.3** EF model snapshot SHALL ตรงกับ model จริง (ไม่มี pending model change) หลังแก้เสร็จ.

### REQ-7 — Canon

- **7.1** `.ai/shared/ARCHITECTURE.md` SHALL บันทึกว่า schema `cfg` ถูกใช้จริงแล้ว (ผู้ใช้แรก =
  MasterData; rf3 จะมาเติม payment config) และ MasterData เป็นโมดูลแยก.
- **7.2** `SchemaNames.Cfg` SHALL มี XML doc บอกว่าใครอยู่ใน schema นี้ และเตือนว่า rf3 จะเพิ่ม
  payment config ตามมา.

## Self-check (5 categories, /spec-analyze inline)

| Category | ผลตรวจ |
|----------|--------|
| Logical inconsistency | REQ-2.2 (type เอกพจน์) เทียบคำขอเดิมของ user ("ตั้งชื่อเป็นพหูพจน์") — user เคาะแล้วว่าหมายถึง namespace/folder ไม่ใช่ type; ไม่ขัดกันแล้ว |
| Ambiguity | "schema ใหม่" ถูก pin เป็น `cfg` (ไม่ใช่ `master` — ชนชื่อ system DB ของ SQL Server และไม่ใช่ 1 ใน 9 schema ที่ล็อกไว้) |
| Conflicting constraint | v5 locked plan เขียนว่า master data อยู่ schema `admin` — REQ-3.1 supersede โดย user ตัดสินตรง 2026-07-13; canon ต้องอัปเดตตาม REQ-7.1 |
| Gap | เดิมไม่ได้ระบุว่าใคร implement port ที่ Admins ใช้ -> ปิดด้วย REQ-4.4; grant/RLS ของ schema ใหม่ -> ปิดด้วย REQ-3.4/3.5 |
| Unstated assumption | ไม่มี prod DB (pre-prod, reset-only) -> จึงแก้ migration ในที่ได้ (REQ-6.1); ยืนยันจาก precedent PR #79 (rf1) และ rf2 |
