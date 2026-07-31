# HANDOFF — products-sp-gateway (rolling)

> Teammate ใหม่: อ่านไฟล์นี้ + requirements.md + design.md + tasks.md ให้จบก่อนแตะโค้ด
> จบ task ของตัวเอง: เติม section ใหม่ **ต่อท้ายไฟล์นี้** (task, สิ่งที่ทำ, decision/deviation, trap ที่เจอ, สถานะ verify, สิ่งที่คนถัดไปต้องรู้)

## Setup (lead, 2026-07-31)

- Branch: `feat/products-sp-gateway` (แตกจาก develop @ b5a7ac6); spec commit `ffea8d4`
- ทำงานบน branch นี้เท่านั้น — commit ได้ ห้าม push / ห้ามแตะ develop (hook `destructive-guard.sh` กันอยู่ — compound command ที่โดน block = ตายทั้งคำสั่ง เช็คว่าส่วนไหนรันไปแล้ว)
- Integration DB: SQL Server container `pol-db` ที่ :11433 (source `.env.integration` ใน Bash call เดียวกับ `dotnet test`)
- Flip task `- [ ]` -> `- [x]` ใน tasks.md ต้องแนบ `Evidence:` block ใน Edit เดียวกัน (gate hook); Evidence header ห้ามมี `-` นำหน้า
- ห้าม emoji ในไฟล์ .md; commit message ภาษาอังกฤษตาม convention + Co-Authored-By Claude
- `.env*` ถูก deny Read/Edit — เข้าถึงผ่าน `git` subcommand เท่านั้นถ้าจำเป็น
- ลบไฟล์ tracked ต้อง `git rm` (rename gate อ่าน git index ไม่ใช่ working tree)

## Task 1 (sp-task1, 2026-07-31)

### สิ่งที่ทำ

- ใหม่: `docker/bootstrap/02-external-sim.sql` — สร้าง `hippodb`/`mammothdb`, `dbo.Documents` (คอลัมน์ §5.2
  ยกเว้น `InsuranceType` + `BranchCode`), filtered unique `UX_Documents_DocumentNo`, SP 2 ตัวตามโครง
  10 ขั้นใน design, `CREATE USER pol_app` + `GRANT EXECUTE`, seed deterministic, self-check ท้ายแต่ละฝั่ง
- จุดเสียบ 4 ที่: `docker-compose.yml` (`pol-db-init` ต่อคำสั่งที่สอง `-C -b`), `docker/migrate-entrypoint.sh`
  (`-N -b` หลัง 01 ก่อน `dotnet ef`), `.github/workflows/ci.yml` (step ใหม่หลัง Bootstrap DB principal),
  `.gitlab-ci.yml` (job `integration`) — ไม่มี env var / compose variable ใหม่
- `docker/migrate-entrypoint.test.sh` เพิ่ม 6 assertion: 02 ถูกเรียก, ลำดับหลัง 01, มี `-N`, มี `-b`, ไม่มี `-C`

### Decision / deviation ที่เกิดตอน implement (ไม่มีข้อไหนขัด design ที่ approve แล้ว)

1. **DB collation = `Thai_CI_AS`** (design ไม่ได้ระบุ) — §5.2 กำหนด `DocumentNo varchar(150)` และเลขเอกสาร
   จริงมีอักษรไทยคั่น (`กธ`/`ตอ`/`ปช`/`อค`/`บต`); ถ้าใช้ collation ปริยายของ instance (CP1252) อักษรไทยใน
   คอลัมน์ varchar จะกลายเป็น `?` เงียบ ๆ ทางเลือกคือทิ้งชนิดตาม §5.2 หรือทิ้งความสมจริงของข้อมูล — เลือก
   คงชนิดแล้วเปลี่ยน collation ของ database แทน และเพิ่ม self-check ยืนยันว่าไทย round-trip
2. **`IF ... CREATE DATABASE` ใช้ไม่ได้** — `CREATE DATABASE` ต้องเป็น statement เดียวใน batch จึงห่อ
   `EXEC(N'CREATE DATABASE ...')` แบบเดียวกับ `01-principals.sql` (snippet ใน design.md เขียนไว้ตรง ๆ จะพัง)
3. **`TOP` + `OFFSET` ในนิพจน์เดียวกันไม่ได้** — design เขียน `SELECT TOP (@PageSize + 1) ... OFFSET ...`;
   ของจริงใช้ `ORDER BY ... OFFSET (...) ROWS FETCH NEXT (@PageSize + 1) ROWS ONLY` ความหมายเดียวกัน
4. **เพิ่ม temp table `#match` ก่อน `#page`** — design พูดถึงแต่ `#page` แต่ EXACT ต้องนับทั้งชุดขณะที่หน้า
   ตัดด้วย OFFSET/FETCH ถ้าไม่ materialize predicate ต้องเขียนซ้ำ 2 ที่ต่อ SP (4 ชุดทั้งไฟล์) แล้ว drift ได้
   เงียบ ๆ; `#match` ทำให้ predicate อยู่ที่เดียว และ TotalRows ของหน้าเกินท้ายชุดยังถูกต้อง
5. **ขอบบน coverage เทียบ `< DATEADD(day, 1, @To)`** — พารามิเตอร์เป็น `date` แต่คอลัมน์เป็น `datetime2(0)`
   ถ้าใช้ `<=` ตรง ๆ เอกสารที่ `StartDate` มีเวลาหลังเที่ยงคืนจะหลุดจากคำว่า inclusive
6. **NonMotor SP คืน `LicensePlateNumber` เป็น `CAST(NULL AS nvarchar(100))` คงที่** ตาม §5.2 ("Non-Motor
   เป็น NULL") และ seed ฝั่ง mammothdb *ใส่ค่าจริงไว้ 1 แถว* (`8ฮฮ 8888`) เพื่อให้ contract test พิสูจน์ได้ว่า
   SP ทั้งไม่ค้นและไม่คืนคอลัมน์นี้
7. **seed idempotent ด้วย `DELETE FROM dbo.Documents` ทั้งตาราง** (ไม่ใช่ลบตาม prefix แบบ `seed-demo.sql`)
   — database พวกนี้มีไว้เป็นตัวจำลองล้วน ไม่มีข้อมูลอื่นให้รักษา
8. `PolicyType` ฝั่ง mammothdb เป็น NULL ทุกแถว (รหัสประเภทกรมธรรม์เป็นแนวคิดฝั่ง Motor/VMI ในแคตตาล็อกนี้)

### Trap ที่เจอ

- **`QUOTED_IDENTIFIER ON` บังคับ** — `dbo.Documents` มี filtered index, DML บนตารางแบบนั้นต้องตั้งค่านี้
  และ session ปริยายของ sqlcmd ไม่ตั้งให้ (กับดักเดียวกับที่ `seed-demo.sql` เขียนเตือนไว้)
- **รูปแบบ DocumentNo ของ ENDORSEMENT ใน `seed-demo.sql` ไม่มี prefix รหัสเซล** (`69900/ปช/...`) — ลอกมาแล้ว
  ชนกติกา prefix `77`/`88` ทันที จับได้เพราะ self-check ไม่ใช่เพราะตาคน
- **ลำดับ `ORDER BY DocumentNo` ไม่ได้เรียงตามเลขลำดับ** — อักษรไทยที่คั่นกลางเป็นตัวตัดสิน
  (`กธ` < `ตอ` < `ปช`) เอกสาร RENEWAL/ENDORSEMENT จึงไปอยู่ท้ายชุดเสมอ; หน้า 1 ของ hippodb เป็นแถว `กธ`
  ล้วน 25 แถว หน้า 2 = `950120`(กธ) + `950004`(ตอ) + `950014`(ปช) — อย่า assert ว่าเรียงตามเลข
- **`.env` อ่านไม่ได้** — ดึงรหัสผ่านจาก environment ของ container แทน:
  `docker inspect pol-db --format '{{range .Config.Env}}{{println .}}{{end}}'` (sa) และ
  `docker inspect pol-core-pol-db-init-1 ...` (`POL_APP_PASSWORD`) โดยไม่ต้องพิมพ์ค่าออกมา
- `docker compose` dev **ไม่ได้รัน migration ให้** — หลัง `down -v` ต้อง `dotnet ef database update` +
  `scripts/seed-demo.sh` เองถึงจะกลับสภาพ (compose ครอบแค่ pol-db + bootstrap + seq)

### ผล verify

- `docker compose down -v && docker compose up -d` -> `pol-core-pol-db-init-1` exited 0, log ปิดท้ายด้วย
  `02-external-sim: hippodb OK (34 documents, 28 in the default search window).` /
  `02-external-sim: mammothdb OK (32 documents, 27 ...)` / `02-external-sim: OK.`
- idempotent: รันซ้ำอีก 2 รอบบน instance เดิม exit 0 ทั้งคู่ ผลลัพธ์เท่าเดิม
- `bash docker/migrate-entrypoint.test.sh` -> `pass=34 fail=0`
- sqlcmd smoke ด้วย login `pol_app` (พิสูจน์ GRANT ไปด้วย): ทั้ง 2 SP คืน 2 result sets ครบ,
  `@CountMode='X'` -> `Msg 50006 ... Invalid CountMode.`
- migrate + seed คืนสภาพแล้ว: EF migrations `Done.`, `seed-demo: OK.` (shop.Products = 500),
  `dotnet test --filter Category=Integration` -> `Passed! Failed: 0, Passed: 47`

### สิ่งที่ task 2 (contract tests) ต้องรู้

**การเชื่อมต่อ**: login `pol_app`, catalog `hippodb` / `mammothdb` (ต้องมี `IntegrationDb.ForCatalog`)

**ค่าที่ seed ไว้**

| แกน | hippodb (Motor) | mammothdb (Non-Motor) |
|---|---|---|
| จำนวนแถวทั้งตาราง | 34 | 32 |
| SaleCode หลัก | `77001` | `S001` |
| SaleCode ตัวปน (1 แถว) | `S001` (DocumentNo `.../950010-10`) | `77001` (`.../960009`) |
| ค้นปริยาย (SaleCode หลัก + UNPAID + ในกรอบเวลา) | TotalRows 28, TotalPages 2, HasNextPage 1 | TotalRows 27, TotalPages 2, HasNextPage 1 |
| SourceSystem | `CMI` / `VMI` | `FIRE` / `MISC` |
| BranchCode | `100` / `200` / `300` / `400` (validate อย่างเดียว ไม่ filter) | เหมือนกัน |
| DocumentNo prefix | ขึ้นต้น `77` เสมอ | ขึ้นต้น `88` เสมอ |

**แถวแกน (axis rows) ที่ตั้งใจให้ assert ตรง ๆ** — เลขลำดับคือส่วนท้าย DocumentNo

- hippodb: `950001`/`950002` ปกติในกรอบ · `950003` นอกกรอบ 6 เดือน · `950004` RENEWAL ใน 2 เดือน (EndDate +30d)
  · `950005` RENEWAL เกิน 2 เดือน (+100d) · `950006` RENEWAL หมดอายุแล้ว (-10d) · `950007`/`950008` PAID
  พร้อม PaidDate · `950009` APPLICATION (VMI เท่านั้น — §1.2 CMI ไม่รองรับ) · `950010` SaleCode `S001`
  · `950011` ShowName มี `%` และ `_` จริง (`บริษัท 100%_มงคลยานยนต์ จำกัด`) · `950013` StartDate ตรงขอบ
  `DATEADD(month, -6, today)` พอดี (inclusive) · `950014` ทะเบียน `9ฮฮ 9999` สำหรับ smart search
  · `950101`-`950120` แถวเติมให้ล้นหน้า 25
- mammothdb: `960001`/`960002` ปกติ · `960003` นอกกรอบ · **`960004` RENEWAL StartDate ในกรอบ แต่ EndDate
  +345d = เข้า** และ **`960005` RENEWAL StartDate นอกกรอบ แต่ EndDate +30d = ไม่เข้า** (คู่นี้คือตัวพิสูจน์ว่า
  Non-Motor ใช้ StartDate 6 เดือนกับทุก DocumentType ไม่ใช่กติกา 2 เดือนของ Motor) · `960006` APPLICATION
  · `960007`/`960008` PAID · `960009` SaleCode `77001` · `960010` ShowName มี `%`/`_` + มีทะเบียน
  `8ฮฮ 8888` เก็บในตารางแต่ SP ต้องไม่ค้นและไม่คืน · `960101`-`960122` แถวเติม

**รูปแบบ field ที่ derive** (ใช้ assert exact-match ได้): `PolicyNumber` = `{SaleCode}-69900/{Seq}` ทุกแถวที่
ไม่ใช่ APPLICATION · `ApplicationNumber` = รูปแบบเดียวกันเฉพาะ APPLICATION · `EndorsementNumber` = `E{Seq}`
· `PreviousPolicyNumber` = `{SaleCode}-68900/{Seq-1}` เฉพาะ RENEWAL/ENDORSEMENT · `PolicyYear` =
`ReferenceYear` = `69` · `ReferenceBranch` = `900` · `NetPremium + Stamp + TaxVat = TotalPremium` เป๊ะ

**พฤติกรรมที่ยิงมือแล้วได้ผลตามนี้** (ใช้เป็นค่าคาดหวังตั้งต้นได้เลย)

- `@PageSize=100` -> RS1 `PageSize` = 25 · `@PageNo=99` -> `28|2|99|25|0|1` + RS2 ว่าง
- `@CountMode='FAST'` -> `TotalRows`/`TotalPages` = NULL, `HasNextPage` = 1, RS2 = 25 แถว
- `@SearchText=N'9ฮฮ'` บน Motor -> 1 แถว (`950014`); `N'8ฮฮ'` บน Non-Motor -> 0 แถว
- `@InsuredName=N'100%'` -> 1 แถวต่อฝั่ง (`950011` / `960010`) — พิสูจน์ LIKE escape
- `@PaidDateFrom='2000-01-01'` -> เหลือเฉพาะ PAID 2 แถวต่อฝั่ง แม้ไม่ได้ส่ง `@PaymentStatus`
- `@PaymentStatus='unpaid'` -> `50007` (BIN2 = case-sensitive) · `@ProductGroup='CMI'` บน Non-Motor -> `50002`
- multi-invalid (`@BranchCode='  '` + `@CountMode='X'`) -> `50004` ตาม fixed order
- ทุก error เป็น `THROW 5000x, N'<msg>', 1` severity 16 -> `SqlException.Number` ตรงเลข
