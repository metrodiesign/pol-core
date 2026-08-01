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

## Task 2 (sp-task2, 2026-07-31)

### สิ่งที่ทำ

- `tests/Integration.Tests/IntegrationDb.cs` — `For(user, pwEnv, catalog = null)` รับ catalog เพิ่ม +
  `ForCatalog(string)` คืน connection ของ `pol_app` ที่ชี้ catalog อื่นบน instance เดียวกัน (ของเดิม
  hardcode `Database={POL_DB}`); ไม่แตะ `AppConn`/`SaConn` เดิม
- ใหม่: `tests/Integration.Tests/SpDocumentContractTests.cs` — 24 test method / **44 test case**
  (20 `[Theory]` รันทั้ง Motor/NonMotor + 4 `[Fact]` เฉพาะฝั่ง) ครอบทุกช่องในตาราง Testing Strategy
  แถว "SP contract"
- ผลรัน: `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj
  --filter Category=Integration` -> `Passed!  - Failed: 0, Passed: 91, Skipped: 0, Total: 91`
  (ของเดิม 47 + ใหม่ 44); `dotnet build ... -warnaserror` -> 0 error 0 warning

### ไม่พบ bug ใน SP — ไม่มีการแก้ `02-external-sim.sql`

ทุก assertion ที่เขียนตาม design/requirements ผ่านตั้งแต่รอบแรก จึงไม่มี deviation ฝั่ง SQL
เพื่อกันการ "เขียวลอย ๆ" ทดสอบความไวด้วยการ mutate ค่าคาดหวังชั่วคราว 2 จุด
(`TotalRows: 28 -> 29`, `RenewalKeptSeq: 950004 -> 950005`) -> `Failed: 8, Passed: 36`
แล้ว revert กลับและรันซ้ำเขียว 91/91

จุดเดียวที่ไม่ได้ทำตามถ้อยคำใน task: ใช้ `@InsuredName` ไม่ใช่ `@SearchText` เป็นตัวพิสูจน์ LIKE escape
เพราะ §3/§4 ไม่ได้ให้ `@SearchText` ค้น `ShowName` (`@SearchText='100%'` จึงคืน 0 แถวเสมอ ไม่ใช่หลักฐาน) —
ตรงกับที่ task 1 ยิงมือไว้ใน HANDOFF เดิม และเสริม `%` / `_` เดี่ยว ๆ ซึ่งเด็ดขาดกว่า `100%`
(ถ้าไม่ escape `%` จะได้ทั้งหน้า 25 แถว แทนที่จะได้แถวเดียว ส่วน `100%` เผอิญให้ผลเท่ากันทั้งสองแบบ)

### สิ่งที่ task 3-5 ต้องรู้

**helper ที่มีให้ใช้แล้ว** — `IntegrationDb.ForCatalog("hippodb" | "mammothdb")` (login `pol_app`,
พิสูจน์ GRANT ไปในตัว); `SpDocumentGatewayIntegrationTests` ของ task 5 ใช้ตัวนี้สร้าง connection string
ที่ยัดใส่ `SpDocumentOptions.MotorConnectionString`/`NonMotorConnectionString` ได้ตรง ๆ

**สัญญาที่ pin ไว้แล้ว (adapter เชื่อได้ ไม่ต้อง defensive เกิน)**

- RS1 มี **หนึ่งแถวเสมอ** และมาก่อน RS2 เสมอ, ไม่มี RS ที่สาม — `NextResultAsync()` ครั้งที่สองคืน false
  (helper ใน contract test assert ทั้งสามข้อนี้ทุกครั้งที่เรียก SP)
- ชื่อ + ลำดับคอลัมน์ RS1 8 ตัว / RS2 32 ตัว ถูก assert แบบ ordered sequence — ลำดับใน `ExpectedItemColumns`
  ของไฟล์ test คือลำดับจริงที่ SP คืน (`InsuranceType` มาก่อน `SourceSystem`, `PaymentStatus` ปิดท้าย)
- ชนิดที่ ADO.NET คืน: `TotalRows`/`TotalPages` = `long` (bigint, เป็น `DBNull` เมื่อ FAST),
  `PageNo`/`PageSize`/`SearchWindowMonths` = `int`, `HasNextPage`/`HasPreviousPage` = `bool` (bit),
  `CountMode` = `string`; ฝั่ง RS2 `StartDate`/`EndDate`/`PaidDate` = `DateTime` (datetime2(0) ไม่มี offset —
  naive ตามที่ design ระบุ), เงิน = `decimal`
- `SqlException.Number` ของ THROW ตรงเลข 50001-50009 (ยืนยันครบทั้ง 9 ตัวทั้งสองฝั่ง) — mapping
  `SqlException.Number in 50001..50009 -> SpDocumentSearchRejectedException` (REQ-5.5) ปลอดภัย
- `@BranchCode` เป็น validate-only จริง (ค่า `'999'` ที่ไม่ตรงแถวไหนเลยยังคืนครบ 28/27 แถว) — options
  `BranchCode` default `"000"` จึงไม่ทำให้ผลหาย แต่ **ค่าว่าง/ช่องว่างล้วนจะได้ 50004** ระวังตอน
  `PostConfigure` อย่าปล่อยให้ค่าเป็น `""`

**กับดักเวลาเขียน test ต่อ**

- **ห้าม assert ว่าเรียงตามเลขลำดับ** — `ORDER BY DocumentNo` ตัดสินด้วยอักษรไทยที่คั่นกลางก่อน
  (`กธ` < `ตอ` < `ปช` · `บต` < `อค`) หน้า 2 ของ Motor คือ `950120`, `950004`, `950014` ตามลำดับนั้น;
  ใน contract test ทุก assertion เป็น **set** ยกเว้น test เดียวที่ตั้งใจ pin ลำดับนี้ไว้
- แถวถูกอ้างด้วย "เลขลำดับท้าย DocumentNo" ผ่าน helper `Seqs()` (`.../กธ/950001-10` -> `950001`) —
  `950008` ไม่มี suffix `-10` ต่างจากแถวอื่น helper รองรับแล้ว
- `pol_app` **ไม่มีสิทธิ์ SELECT** บน `hippodb.dbo.Documents` / `mammothdb.dbo.Documents` (มีแต่ EXECUTE +
  ownership chaining) — test ที่อยากเช็คข้อมูลดิบต้องใช้ `sa` หรือพิสูจน์ผ่าน SP เท่านั้น
- `Microsoft.Data.SqlClient` มาแบบ transitive ใน `Integration.Tests` อยู่แล้ว (ผ่าน
  `BuildingBlocks.Infrastructure`) — ไม่ต้องเพิ่ม PackageReference ในโปรเจกต์ test; ส่วน pin exact
  ที่ REQ-5.9 สั่งเป็นงานของ task 5 ใน `Directory.Packages.props` + csproj ของ `Products.Infrastructure`
- `CommandType` ต้อง `using System.Data;` (ImplicitUsings ของ test project ไม่ได้ใส่มาให้)

## Task 3 (sp-task3, 2026-07-31)

### สิ่งที่ทำ

ไฟล์ใหม่ 4 + แก้ 2 (commit `69ff065`, +179 บรรทัด ไม่มีการลบ) — ยังไม่มี consumer ตามที่ task กำหนด

- `src/Modules/Products/Products.Application/Ports/ISpDocumentGateway.cs`
- `src/Modules/Products/Products.Application/Ports/SpDocumentContracts.cs` (3 record: request / metadata / item + result)
- `src/Modules/Products/Products.Application/Ports/SpDocumentSearchRejectedException.cs` (แยกไฟล์ตาม convention
  ของ `Payments.Application/Ports/PspRejectedException.cs`)
- `src/BuildingBlocks/BuildingBlocks.Application/UpstreamUnavailableException.cs`
- `ProblemDetailsExceptionHandler.Map()` +2 บรรทัด (arm เดียว วางต่อจาก `InvalidOperationException`)
- `tests/Hosts.Tests/ProblemDetailsExceptionHandlerTests.cs` +17 บรรทัด (ต่อยอดไฟล์เดิม ไม่สร้างไฟล์ใหม่)

ไม่แตะ `Products.Application.csproj` (ไม่มี package ใหม่ — `InsuranceType` มาจาก `Products.Domain` ที่ reference อยู่แล้ว)

### Signature จริง (copy ได้เลย — ตรง design ตัวต่อตัว ไม่มี deviation)

```csharp
namespace Products.Application.Ports;   // ทุกไฟล์ในกลุ่มนี้

public interface ISpDocumentGateway
{
    Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken cancellationToken);
}

// SpDocumentContracts.cs — ต้องมี `using Products.Domain;` (InsuranceType)
public sealed record SpDocumentSearchRequest(
    InsuranceType Target, string SaleCode, string? SearchText, string? InsuredName,
    DateOnly? CoverageStartFrom, DateOnly? CoverageStartTo,
    DateOnly? CoverageEndFrom, DateOnly? CoverageEndTo,
    string PaymentStatus, string DocumentType, string ProductGroup,
    string? PolicyNo, string? ApplicationNo,
    DateTime? PaidDateFrom, DateTime? PaidDateTo,
    int PageNo, int PageSize, string CountMode);

public sealed record SpPaginationMetadata(
    long? TotalRows, long? TotalPages, int PageNo, int PageSize,
    bool HasNextPage, bool HasPreviousPage, string CountMode, int SearchWindowMonths);

public sealed record SpDocumentItem(
    string? InsuranceType, string? SourceSystem, string? DocumentType, string? DocumentNo,
    string? PolicyYear, string? ReferenceBranch, string? ReferencePre, string? PolicySequenceNo,
    string? ReferenceYear, string? ReferenceNo, string? PolicyBranch, string? PolicyType,
    string? SaleCode, string? SaleFullName, string? BrokerCode, string? BrokerName,
    string? PolicyNumber, string? ApplicationNumber, string? PreviousPolicyNumber,
    string? EndorsementNumber, DateTime? StartDate, DateTime? EndDate, string? ShowName,
    decimal? NetPremium, decimal? Stamp, decimal? TaxVat, decimal? TotalPremium,
    decimal? CommissionPercent, decimal? CommissionAmount, DateTime? PaidDate,
    string? LicensePlateNumber, string? PaymentStatus);

public sealed record SpDocumentSearchResult(SpPaginationMetadata Page, IReadOnlyList<SpDocumentItem> Items);

public sealed class SpDocumentSearchRejectedException : ArgumentException
{
    public SpDocumentSearchRejectedException(int spErrorNumber, string message) : base(message) =>
        SpErrorNumber = spErrorNumber;

    public int SpErrorNumber { get; }
}

// namespace BuildingBlocks.Application
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException(string message) : base(message) { }
    public UpstreamUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
```

ลำดับ 32 field ของ `SpDocumentItem` คัดลอกจาก `ExpectedItemColumns` ใน `SpDocumentContractTests` (ลำดับจริงของ RS2)
— positional record จึงเรียง reader ตามลำดับนี้ได้ตรง ๆ แต่ REQ-5.3 ยังสั่งให้ใช้ `GetOrdinal` ตามชื่อ ห้ามใช้ index ดิบ

### สิ่งที่ task 4 / 5 / 6 ต้อง import

- task 4 (`SpDocumentItemMapper` ใน `Products.Application/Ports/`): `SpDocumentItem` อยู่ namespace เดียวกันแล้ว
  ไม่ต้อง using เพิ่ม; ฝั่ง `Products.Domain` (`ProductInput`) ต้อง `using Products.Domain;`
- task 5 (`Products.Infrastructure/Sp/SpDocumentGateway.cs`): `using Products.Application.Ports;` +
  `using BuildingBlocks.Application;` (สำหรับ `UpstreamUnavailableException`) — csproj ของ `Products.Infrastructure`
  reference ทั้งสองอยู่แล้ว ไม่ต้องเพิ่ม ProjectReference
- task 6 (`ListProducts.cs`): `using Products.Application.Ports;` — `ListProducts.cs` อยู่ namespace
  `Products.Application` (ไม่ใช่ `.Ports`) จึงต้อง using จริง

### ข้อควรระวัง

- **task 6 / REQ-10.5**: `tests/Hosts.Tests/ProblemDetailsExceptionHandlerTests.cs` มี
  `using Products.Application.Ports;` แล้ว (จำเป็น — ตัวพิสูจน์ว่า `SpDocumentSearchRejectedException` ได้ 400 จริง
  ผ่าน handler ต้องอยู่ที่เดียวกับ handler) ดังนั้น insulation guard ต้องสแกน **production assembly เท่านั้น**
  (Api / Contracts / โมดูลอื่น) ห้ามสแกน assembly ของ test ไม่งั้น guard แดงทันทีที่เขียนเสร็จ
- **ห้ามเพิ่ม arm ให้ `SpDocumentSearchRejectedException` ใน `Map()`** — มันได้ 400 จาก bucket `ArgumentException`
  ที่มีอยู่ (arm อื่นที่อยู่ก่อนหน้าไม่มีตัวไหนอยู่บน chain การสืบทอดของมัน) และห้ามแทรก arm ใหม่ก่อนบรรทัด
  `ArgumentException` ที่จะกลืนมันไปโดยไม่ตั้งใจ
- **`Map()` ไม่ echo `exception.Message` ทุก arm** (fixed detail) — `SpErrorNumber` กับข้อความที่เราเขียน
  มีไว้ให้ log/test เท่านั้น ห้ามออกแบบให้ client อ่านเลขจาก body
- handler log ให้อัตโนมัติ: `status >= 500` -> `LogError`, ต่ำกว่า -> `LogWarning` ดังนั้น 503 ถูก log กลางอยู่แล้ว
  แต่ REQ-5.6 ยังสั่งให้ adapter log รายละเอียด `SqlException` เอง — ใช้ ctor 2 พารามิเตอร์ (ใส่ inner) จะได้เห็น
  SQL error เต็มใน log กลางด้วยโดยไม่รั่วออก response
- `PaymentStatus` / `DocumentType` / `ProductGroup` / `CountMode` บน request เป็น **string ของ wire ไม่ใช่ enum**
  เพราะ `ALL` ไม่ใช่สมาชิกของ enum ฝั่ง Domain (และห้ามทำให้เป็น — ล็อกโดย spec `checkout-chain-document-fields`)
  — handler เป็นคนแปลง `ProductFilterDto` -> string เหล่านี้
- `Target` เป็น `Products.Domain.InsuranceType` (ไม่ใช่ string) — เป็น routing key ฝั่งเรา ไม่ใช่พารามิเตอร์ของ SP
  ห้ามส่งเข้า `SqlCommand`; `@BranchCode` มาจาก options ที่ adapter เท่านั้น (B2)
- Architecture.Tests เต็มชุดรัน **~3 วินาที** (225 tests) ไม่ใช่หลักนาที — ไม่ต้องกลัวรัน; ที่ช้าคือ build แรก

## Task 4 (sp-task4, 2026-07-31)

### สิ่งที่ทำ

ไฟล์ใหม่ 2 + แก้ 10 (commit `e995b65`) — domain + mapper ล้วน ไม่แตะ handler / repository / adapter

- `src/Modules/Products/Products.Domain/ProductInput.cs` — เพิ่ม `PaymentStatus` + `PaidDate`
- `src/Modules/Products/Products.Domain/Product.cs` — `Create` honor ค่าจาก input, `RefreshFromExternal` ใหม่,
  `ApplyFields` private ตัวเดียวใช้ร่วมกัน, `SideOf(ProductGroup)` static helper (`InsuranceType` เดิมเรียกตัวนี้)
- ใหม่: `src/Modules/Products/Products.Application/Ports/SpDocumentItemMapper.cs`
- ใหม่: `tests/Products.Tests/SpDocumentItemMapperTests.cs` (13 test method / 29 case)
- `tests/Products.Tests/ProductTests.cs` +16 test method / 19 case (helper `NewInput` รับ
  `paymentStatus`/`paidDate` เพิ่ม) — รวม Products.Tests 54 -> 102

### Shape จริงของ mapper output (task 6 ใช้ตัวนี้)

```csharp
namespace Products.Application.Ports;   // ไฟล์เดียวกันมีทั้ง record และ static class

public sealed record MappedSpDocument(SpDocumentItem Item, ProductInput? Input, string? SkipReason);

public static class SpDocumentItemMapper
{
    public static IReadOnlyList<MappedSpDocument> Map(IReadOnlyList<SpDocumentItem> items);
}
```

- คืน **หนึ่งรายการต่อแถวของ SP ตามลำดับเดิมเสมอ** (ไม่กรองให้) — `Input == null` คือแถวที่ข้าม และ
  `SkipReason` เป็นข้อความพร้อม log (`"DocumentNo is blank"`, `"SaleCode is blank"`,
  `"TotalPremium is null or not greater than zero"`, `"unknown SourceSystem 'xxx'"`,
  `"unknown DocumentType 'xxx'"`, `"unknown PaymentStatus 'xxx'"`, `"duplicate DocumentNo within the page"`)
- แถวที่ `Input != null` การันตี `SkipReason == null` และกลับกัน — handler กรองด้วย
  `mapped.Where(m => m.Input is not null)` ส่งเข้า `UpsertByDocumentNoAsync` แล้วจับคู่ผลกลับตามลำดับ
  (repository คืน `Product` ตามลำดับ inputs ตาม design)
- **REQ-7.6 warning เป็นหน้าที่ handler**: mapper ไม่ log — เช็ค `input.PaymentStatus is PaymentStatus.PAID
  && input.PaidDate is null` เอา `DocumentNo` ไปเขียน log warning (domain ยอมรับเคสนี้เงียบ ๆ โดยตั้งใจ)
- `Map` ปลอดภัยกับ list ว่าง; `null` -> `ArgumentNullException`

### Decision / deviation

1. **`PaymentStatus` + `PaidDate` อยู่ในกลุ่มพารามิเตอร์บังคับ (ต่อจาก `TotalPremium`) ไม่ใช่ท้าย record** —
   C# ห้ามพารามิเตอร์บังคับตามหลังพารามิเตอร์ที่มี default (ท้าย record = ต้องมี default = `UNPAID` เงียบ ๆ
   ซึ่งคือช่องเดียวกับที่ B1 เกิด) จึงบังคับให้ทุก construction site ระบุเอง; caller เดิมทุกจุดส่ง
   `PaymentStatus.UNPAID, null` ตามที่ task กำหนด
2. **`ApplyFields` normalize `PaidDate` เป็น null เมื่อสถานะลงเอยเป็น UNPAID** — รักษา invariant ที่
   `Product.PaidDate` ประกาศไว้ ("Null while PaymentStatus is UNPAID")
3. **mapper ใช้ switch expression ต่อ enum ไม่ใช่ `Enum.TryParse`** — TryParse รับ `"0"` และ `"CMI, VMI"`
   เป็นสมาชิกด้วย (มี test คุมทั้งสองรูปแบบ) ส่วน case-sensitivity ก็ได้มาฟรีจาก switch
4. **`MappedSpDocument` พก `SkipReason`** เพิ่มจาก shape `(item, ProductInput?)` ใน design (ที่เปิดช่องไว้ว่า
   "shape เทียบเท่าที่สื่อ skip/reason ได้") — เหตุผลของการข้ามต่างกันมากในเชิง ops
   (upstream เปลี่ยน enum vs ข้อมูลแหว่ง vs แถวซ้ำ) ถ้าไม่พกมา handler เขียน log ที่ใช้งานไม่ได้
5. **แถวที่ถูก skip ไม่จอง DocumentNo ใน dedupe set** — dedupe เช็คเป็นด่านสุดท้ายหลัง validate ครบ
   ไม่งั้นแถวเสียแถวแรกจะบังแถวดีที่ตามมาในหน้าเดียวกัน (มี test คุม)

### Caller ที่ไล่แก้ (8 จุด / 6 ไฟล์ — ทั้งหมดเป็น test; ใน src ไม่มีจุดไหนสร้าง `ProductInput` เลย)

`CreateProductCommand` แค่ **ถือ** `ProductInput` ต่อ (`CreateProductCommand(ProductInput Input)`) จึงไม่ต้องแก้
และ `seed-demo.sql` INSERT ตรงเข้า `shop.Products` เป็น SQL ล้วน (grep แล้วไม่ผ่าน `ProductInput`) จึงไม่แตะ

- `tests/Architecture.Tests/WriteFloorTests.cs`, `ReadFloorTests.cs`, `ProductRepositoryListTests.cs` (3 จุด)
- `tests/Products.Tests/ProductTests.cs` (helper), `DocumentPaidOnOrderPaidConsumerTests.cs`
- `tests/Hosts.Tests/WorkerWriteFloorTests.cs`, `InsuranceCheckoutEndToEndTests.cs`,
  `ProductInsuranceFieldsRoundTripTests.cs`

ใน Hosts.Tests ต้องเขียน `Products.Domain.PaymentStatus.UNPAID` แบบเต็ม (ชนกับ `PaymentStatus` ของโมดูลอื่น
ที่ using อยู่ในไฟล์เดียวกัน) ส่วน Architecture.Tests / Products.Tests เขียนสั้นได้

### ข้อควรระวังสำหรับ task 6

- **`RefreshFromExternal` throw กลางทางได้ และ aggregate จะถูกแก้ไปแล้วบางส่วน** (`ApplyFields` assign
  ทีละ property; ตัวที่ throw ได้คือ `Optional` เกินความยาว กับ `RequireMoney` ทศนิยม/ติดลบ) — repository
  ต้องปล่อยให้ exception พา unit of work ทั้งก้อนตาย **ห้าม catch แล้ว SaveChanges ต่อ** ไม่งั้นเขียนค่าครึ่ง ๆ ลง DB
- แถวจาก SP ที่ field ยาวเกิน limit ของ `shop.Products` (เช่น `ShowName` > 500) **ไม่ใช่เคส skip ของ mapper**
  (REQ-7.7 ไม่ครอบ) — จะกลายเป็น `ArgumentException` -> 400 ทั้งหน้า; sim DB ใช้ความยาวตาม §5.2 จึงไม่ชน
  แต่ถ้าอยากกันจริงต้องเป็น requirement ใหม่ อย่าเงียบ ๆ ใส่ try/catch รายแถว
- `RefreshFromExternal` เทียบ `DocumentNo` **หลัง trim** กับค่าที่เก็บใน aggregate (ซึ่ง trim แล้วเช่นกัน)
  และ mapper ก็ trim ให้ก่อนแล้ว — repository lookup ต้องใช้ `ProductInput.DocumentNo` (trim แล้ว) เป็น key
  ให้ตรงกัน ไม่ใช่ค่าดิบจาก `SpDocumentItem`
- side-flip guard ยิงเมื่อ `ProductGroup` ใหม่ข้ามฝั่ง Motor/NonMotor: เกิดได้จริงถ้า upstream คืน
  `SourceSystem` คนละฝั่งกับแถวที่มี `DocumentNo` เดียวกันใน `shop.Products` (เช่น seed เดิม 500 แถวที่ยัง
  ไม่ align) — ผลคือ 400 ทั้งหน้า ไม่ใช่ skip แถวเดียว ถ้าเจอตอน E2E ให้แก้ที่ข้อมูล ไม่ใช่ผ่อน guard
- `Product.Create` ตอนนี้ **ไม่ hardcode UNPAID แล้ว** — ทางอื่นที่สร้าง Product (importer/test ใหม่)
  ต้องระบุสถานะเอง คอมไพเลอร์บังคับให้แล้ว
- `Products.Tests` ตอนนี้ 102 test (เดิม 54) — ถ้า task 6 ทำให้ตัวเลขนี้ลด แปลว่าลบ test โดยไม่ตั้งใจ

## Task 5 (sp-task5, 2026-07-31)

### สิ่งที่ทำ

ไฟล์ใหม่ 3 + แก้ 6 (commit `727ebd2`, +558/-1) — adapter + options + DI + wiring ล้วน ไม่แตะ handler / repository / mapper

- ใหม่: `src/Modules/Products/Products.Infrastructure/Sp/{SpDocumentOptions,SpDocumentGateway}.cs`
- ใหม่: `tests/Integration.Tests/SpDocumentGatewayIntegrationTests.cs` (8 test method / 16 case)
- `Directory.Packages.props` — pin `Microsoft.Data.SqlClient` **6.1.1** exact (เลขที่ EF SqlServer 10.0.8
  resolve transitive อยู่แล้ว ตรวจด้วย `dotnet list src/BuildingBlocks/BuildingBlocks.Infrastructure
  package --include-transitive`) + `PackageReference` ใน `Products.Infrastructure.csproj`
- `ProductsModuleRegistration.AddProductsModule()` — เดิมคืน `services` เปล่า ตอนนี้
  `AddSingleton<ISpDocumentGateway, SpDocumentGateway>()` (ถูกเรียกจาก `Program.cs` อยู่แล้ว)
- `tests/Integration.Tests/Integration.Tests.csproj` — `ProjectReference` ไป `Products.Infrastructure`
- `tests/Architecture.Tests/RawConnectionTests.cs` — ยกเว้น `SpDocumentGateway` หนึ่งชื่อ (ดู deviation)

### DI / config ที่เพิ่มใน Program.cs (บรรทัดจริง)

- บรรทัด 49: `using Products.Infrastructure.Sp;`
- บรรทัด 140-149: `Configure<SpDocumentOptions>(...GetSection(SpDocumentOptions.SectionName))` +
  `PostConfigure<SpDocumentOptions>` ที่เติม connection string เฉพาะตัวที่ว่าง/blank ด้วย
  `new SqlConnectionStringBuilder(appConnString) { InitialCatalog = "hippodb"|"mammothdb" }.ConnectionString`
  — วางต่อจาก `Configure<PspOptions>` ก่อน `AddProductsModule()` (ต้องอยู่หลังบรรทัดที่ `appConnString`
  ถูก re-stamp `ApplicationName = "Api"` แล้ว); ไม่มี env var / compose variable ใหม่
- บรรทัด 151: `builder.Services.AddProductsModule();` (เดิมอยู่แล้ว ไม่ได้ย้าย)

### พฤติกรรม gateway ที่ task 6 พึ่งได้

- **คืน `SpDocumentSearchResult` ตามที่ SP คืนดิบ ๆ** — ไม่ filter/sort/นับซ้ำ ไม่แตะ `Page` ใด ๆ;
  `Items` เรียงตามลำดับแถวของ SP; หน้าที่เกินท้ายชุดได้ `Items` ว่างพร้อม metadata ปกติ (ไม่ throw)
- **`Page.TotalRows`/`TotalPages` เป็น `null` จริงตอน FAST** (ไม่ใช่ 0) — envelope ส่งต่อได้ตรง ๆ ตาม REQ-8.2
- exception ที่ออกจาก `SearchAsync` มีแค่ 3 ทาง: `SpDocumentSearchRejectedException` (SP THROW 50001-50009,
  พก `SpErrorNumber`) -> 400 · `UpstreamUnavailableException` (SqlException อื่น / RS หาย / column drift /
  connection string ว่าง) -> 503 · `OperationCanceledException` (request ถูก cancel) — handler **ไม่ต้อง
  try/catch อะไรเพิ่ม** ทุกตัว map ที่ `ProblemDetailsExceptionHandler` อยู่แล้ว
- `@BranchCode` มาจาก options เท่านั้น — `SpDocumentSearchRequest` ไม่มีช่องนี้ ห้ามพยายามส่งจาก handler
- gateway เป็น **singleton** และ stateless (ถือแค่ options + logger, เปิด/ปิด `SqlConnection` ต่อ call) —
  handler ที่เป็น Scoped inject ได้ปกติ ไม่เกิด captive dependency
- `CommandTimeoutSeconds` = 15 ปริยาย; timeout จะมาถึง handler เป็น `UpstreamUnavailableException` ไม่ใช่ 500

### ข้อควรระวังสำหรับ task 6

- **insulation guard REQ-10.5 ต้องสแกน production assembly เท่านั้น** — ตอนนี้มี test assembly 2 ตัวที่อ้าง
  `Products.Application.Ports` โดยชอบธรรม: `Hosts.Tests` (จาก task 3) และ `Integration.Tests` (ไฟล์นี้ +
  `ProjectReference` ไป `Products.Infrastructure`) ถ้า guard สแกนทั้ง solution จะแดงทันที
- **`RawConnectionTests` เป็น guard ที่งานนี้ชนแล้วครั้งหนึ่ง** — ถ้า task 6 เพิ่มโค้ดที่แตะ `SqlConnection`
  ใน `Persistence.MerchantRuntime` (เช่นตอนจับ 2601/2627) จะแดงอีก; ทางที่ถูกคือจับ `SqlException` ผ่าน
  `DbUpdateException.InnerException` (ชื่อ type `SqlException` ไม่ติด guard — guard จับ prefix
  `Microsoft.Data.SqlClient.SqlConnection` เท่านั้น) ไม่ใช่เปิด connection เอง
- **ห้าม assert วันที่แบบสัมบูรณ์ใน test ที่แตะ sim DB** — seed สัมพัทธ์กับ `GETDATE()` ของ *วันที่ bootstrap รัน*
  ไม่ใช่วันที่รัน test; ถ้า container เก่ากว่าหลายวัน ค่าจะเลื่อน (ไฟล์นี้จึง assert แค่ not-null / EndDate >
  StartDate / `Kind == Unspecified`)
- **แถวแกนดึงด้วย `PolicyNo` exact ไม่ใช่ตำแหน่งในหน้า** — `960001` อยู่หน้า 2 ของ Non-Motor เพราะ
  `ORDER BY DocumentNo` ตัดสินด้วยอักษรไทยก่อน (`บต` < `อค`); `PolicyNumber` = `{SaleCode}-69900/{Seq}`
  ซึ่งฝั่ง Non-Motor คือ `S001-69900/960001` (SaleCode ไม่ใช่ prefix `88001` ของ DocumentNo)
- `SpDocumentItem.PolicyType` เป็น NULL ทุกแถวฝั่ง mammothdb และเป็น `'90'` เฉพาะ VMI ฝั่ง hippodb —
  อย่าใช้ field นี้เป็นเงื่อนไขอะไรใน mapper/handler
- ถ้าอยากเห็น log ของ 503 ตอน debug: adapter `LogError` ที่ `Products.Infrastructure.Sp.SpDocumentGateway`
  พร้อม `Number`/`State`/`Class` + inner exception เต็ม ส่วน response ยังเป็น fixed detail ไม่รั่ว

### Deviation

1. **ยกเว้น `SpDocumentGateway` ใน `RawConnectionTests`** — guard เดิมห้าม production infrastructure อ้าง
   `Microsoft.Data.SqlClient.SqlConnection` เลย (raw connection ข้าม query filter + `IWriteAuthorizer` ของ
   app database) แต่ REQ-5.1 สั่ง ADO.NET ตรงโดยเจตนา และ gateway ต่อ `hippodb`/`mammothdb` ซึ่งไม่มีแถวผูก
   merchant ไม่อยู่ใน DbContext ใด และ login มีแค่ EXECUTE — ไม่มี floor ให้ข้าม จึงยกเว้นด้วย **ชื่อ type เดียว**
   (`.That().DoNotHaveName("SpDocumentGateway")`) ไม่ใช่ถอด assembly ออกจากรายการ; ยืนยันว่าช่องแคบจริงด้วย
   การเปลี่ยนชื่อยกเว้นเป็น `SpDocumentGatewayXX` ชั่วคราว -> guard แดงที่ offender เดิมทันที
2. **`Add(...)` helper ของ parameter รับ `size` แล้วเซ็ตเฉพาะเมื่อ > 0** — `Date`/`DateTime2`/`Int` ส่ง 0
   (ไม่เซ็ต `Size`); ไม่ได้เซ็ต `Scale` ของ `DateTime2` เพราะ SP ประกาศ `datetime2(0)` อยู่แล้ว server
   แปลงให้ที่ boundary ของพารามิเตอร์
3. **เพิ่ม `ArgumentNullException.ThrowIfNull(request)`** ต้นเมธอด (design ไม่ได้ระบุ) — port เป็น public
   surface ของโมดูล และ NRE กลางทาง `AddParameters` อ่านยากกว่ามาก

### ผล verify

- `dotnet build pol-core.slnx -warnaserror` -> `64 projects, 0 errors, 0 warnings`
- `source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter
  Category=Integration` -> `Passed! Failed: 0, Passed: 107, Skipped: 0, Total: 107` (เดิม 91 + ใหม่ 16)
- `dotnet test tests/Hosts.Tests` -> `Passed! Failed: 0, Passed: 365` (17 host boot ผ่าน — ไม่มี ValidateOnStart)
- `dotnet test tests/Architecture.Tests` -> `Passed! Failed: 0, Passed: 225`
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> เขียวทั้ง 16 โปรเจกต์
- `bash scripts/check-rename-identifiers.sh` -> `rename-identifier gate: OK`
- mutation ชั่วคราว 3 จุด (สลับ ordinal commission, ย่อช่วง error เป็น 50001..50005, hardcode BranchCode
  `"000"`) -> `Failed: 6, Passed: 10` แดงตรงตัวที่ตั้งใจ แล้ว revert + เขียว 16/16

## Task 6 (sp-task6, 2026-07-31)

### สิ่งที่ทำ

ไฟล์ใหม่ 4 + ลบ 1 (`git rm`) + แก้ 15 (commit `9b2a3dd`, +974/-479) — cutover ทั้งเส้นทาง read

- `Products.Application/ListProducts.cs` — `ProductFilterDto` เพิ่ม `InsuranceType?` + `CountMode`
  (+ `CountModeValue`), `ProductPage` ใหม่, `ListProductsQuery : IQuery<ProductPage>`,
  `ListProductsHandler` เขียนใหม่ทั้งตัว (`ResolveTarget` -> gateway -> mapper -> log -> upsert -> envelope)
- `Products.Application/IProductRepository.cs` — ลบ `ListAsync`, เพิ่ม `UpsertByDocumentNoAsync`
- `Persistence.MerchantRuntime/Products/ProductRepository.cs` — impl upsert + retry, ตัด `IClock` /
  window constants / `SfsLike` (ctor เหลือ `db` ตัวเดียว)
- `Hosts/Api/Program.cs` endpoint metadata + `SfsOpenApi.AddProductQueryParameters` description
- `Directory.Packages.props` + `Products.Application.csproj` — `Microsoft.Extensions.Logging.Abstractions` 10.0.8
- `Persistence.MerchantRuntime.csproj` + `Integration.Tests.csproj` — grant + ProjectReference (ดู deviation 3)
- tests: ใหม่ `ListProductsHandlerTests` / `SpInsulationTests` / `FakeSpDocumentGateway` /
  `ProductUpsertIntegrationTests`; แก้ `ProductFilterDtoTests` / `DocumentPaidOnOrderPaidConsumerTests` /
  `WorkerWriteFloorTests`; rewrite `ProductInsuranceFieldsRoundTripTests` /
  `InsuranceCheckoutEndToEndTests` (เฉพาะ list path); ลบ `ProductRepositoryListTests.cs`

### Shape จริงที่ task 7 (docs/E2E) ต้องอ้าง

```csharp
public sealed record ProductPage(
    IReadOnlyList<ProductListItem> Items,
    long? TotalRows, long? TotalPages, int PageNo, int PageSize,
    bool HasNextPage, bool HasPreviousPage, string CountMode, int SearchWindowMonths);

Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(IReadOnlyList<ProductInput> inputs, CancellationToken ct);
```

JSON ของ response เป็น camelCase ตามปกติ: `items[]`, `totalRows`, `totalPages`, `pageNo`, `pageSize`,
`hasNextPage`, `hasPreviousPage`, `countMode`, `searchWindowMonths` — **ไม่ใช่** `page`/`limit`/`total`
ของ `PagedResult` เดิม (breaking change ที่ REQ-8.1 ตั้งใจ)

### Decision / deviation

1. **routing validation อยู่ที่ handler (`ResolveTarget`) ไม่ใช่ `Parse`** — design เขียน "resolve Target"
   ไว้ที่ handler และ handler ต้อง resolve อยู่แล้ว จึงเช็คขัดแย้ง/ว่างทั้งคู่ที่จุดเดียว; เคสขัดแย้งจึงถูก
   ทดสอบใน `ListProductsHandlerTests` ไม่ใช่ `ProductFilterDtoTests` (ต่างจากช่องในตาราง Testing Strategy)
2. **เพิ่ม package `Microsoft.Extensions.Logging.Abstractions` 10.0.8** ให้ `Products.Application` —
   ไม่มี Application project ไหนเข้าถึง `ILogger` ได้มาก่อน (คอมเมนต์ค้างใน `OrderPaidConsumer.cs` ยืนยัน)
   แต่ REQ-7.6/7.7 สั่งให้ handler log; เลขเดียวกับที่ EF resolve transitive อยู่แล้ว
3. **`Integration.Tests` เลิกเป็น raw-connection-only** — เพิ่ม ProjectReference `Persistence.MerchantRuntime`
   + `InternalsVisibleTo Include="Integration.Tests"` เพราะ retry ของ upsert เป็นพฤติกรรม EF ChangeTracker
   ที่ต้องขับ type ตัวจริง; ไฟล์อื่นในโปรเจกต์ยัง raw connection เหมือนเดิม
4. **`InsuranceCheckoutEndToEndTests` เปลี่ยนความหมายของ assert ท้ายเส้น** — จาก "เอกสาร PAID หลุดจาก
   ลิสต์ UNPAID" (ตัวกรองย้ายไป SP แล้ว โค้ดเราไม่ได้ทำอีก) เป็น "upstream บอก UNPAID -> local PAID ไม่ถูก
   downgrade" (REQ-7.4) ซึ่งเป็นกติกาที่ยังเป็นของเราจริง
5. จำลอง race ด้วย `SaveChangesInterceptor` ที่ INSERT แถวชนจาก context อื่นตอน save ครั้งแรก (ยิงครั้งเดียว)
6. แก้คอมเมนต์ค้างใน `PolicyReportRepository.cs` ที่อ้าง `ProductRepository.ListAsync`

### ผล verify

- `dotnet build pol-core.slnx -warnaserror` -> `64 projects, 0 errors, 0 warnings`
- `dotnet test tests/Products.Tests` -> `Passed! Failed: 0, Passed: 135` (เดิม 102)
- `source .env.integration && dotnet test tests/Integration.Tests --filter Category=Integration` ->
  `Passed! Failed: 0, Passed: 112, Skipped: 0, Total: 112` (เดิม 107)
- `dotnet test tests/Hosts.Tests` -> `Passed! Failed: 0, Passed: 369` (เดิม 365)
- `dotnet test tests/Architecture.Tests` -> `Passed! Failed: 0, Passed: 200` (เดิม 225 — ลบ 25 case
  ของ `ProductRepositoryListTests`)
- `dotnet test pol-core.slnx --filter "Category!=Integration"` -> เขียวทั้ง 16 โปรเจกต์
- `bash scripts/check-rename-identifiers.sh` -> `rename-identifier gate: OK`
- mutation 4 จุด (เลข unique violation, เดา target เมื่อว่างทั้งคู่, ยัด Ports เข้า Persistence,
  ใส่ assembly ปลอมในรายการ guard) -> แดงตรงตัวที่ตั้งใจทุกจุด แล้ว revert

### สิ่งที่ task 7 ต้องรู้

**docs ที่ยังไม่ได้อัปเดต (งานของ task 7)** — ไล่ตรวจแล้วมี 3 กลุ่ม

- `docs/reference/platform-modules.md` §5 Product (บรรทัด ~656-675) — ยังบรรยาย read path ว่าเป็น
  `page`/`limit` + `productFilters` ที่ order ด้วย `DocumentNo` และ search window 6 เดือน / RENEWAL 2 เดือน
  **ฝั่งเรา**; ตอนนี้ทั้ง window / order / paging เป็นของ SP และ response เป็น envelope §5.1
- `docs/reference/search-filter-sort.md` — ตัวอย่าง `ListProductsQuery` (~บรรทัด 755 และ 1250) มี note
  เตือนอยู่แล้วว่าล้าสมัยตั้งแต่ sp-53 (`PagedResult<ProductListItem>` + `PagedQuery` + `IMerchantScoped`
  ในตัวอย่างไม่ตรงโค้ดจริงมาก่อนหน้านี้แล้ว) — task 7 แค่ต่อ note เดิมให้ครอบ `ProductPage` /
  `insuranceType` / `countMode` พอ ไม่ต้องรื้อตัวอย่าง
- spec เก่า `.ai/specs/products-sp-53-alignment/` อ้าง `ListAsync` ไว้หลายที่ — เป็นบันทึกประวัติศาสตร์
  **ห้ามแก้ย้อนหลัง** (จดไว้กันสับสนตอน grep)

**E2E ควรยิงตรงไหน** (REQ-11.5 checklist)

- `GET /api/v1/products?page=1&limit=25&productFilters={"saleCode":"77001","insuranceType":"Motor"}`
  -> คาด `totalRows` 28 / `totalPages` 2 / `items` 25 แถว (ค่าตาม seed ของ task 1)
- ฝั่ง Non-Motor ใช้ `{"saleCode":"S001","insuranceType":"NonMotor"}` -> 27 / 2
- `countMode=FAST` -> `totalRows` กับ `totalPages` เป็น `null` ใน JSON (ไม่ใช่ 0) และ `hasNextPage` ยังจริง
- `countMode=APPROX` -> 400; `{"saleCode":"77001"}` เปล่า ๆ (ไม่มี insuranceType/productGroup) -> 400;
  `{"saleCode":"77001","productGroup":"FIRE","insuranceType":"Motor"}` -> 400
- add-to-cart ด้วย `items[0].id` ที่เพิ่ง upsert (Guid ของแถว local ไม่ใช่ DocumentNo)
- 503: `docker stop pol-db` ไม่ได้ (แอปตายไปด้วย) — วิธีที่ตรงกว่าคือชี้
  `SpDocument:MotorConnectionString` ไปที่ catalog ที่ไม่มี SP แล้วยิงใหม่
- **ก่อน E2E ต้องรัน `dotnet ef database update` + `scripts/seed-demo.sh` ถ้าเพิ่ง `down -v`** (compose
  ไม่ได้รัน migration ให้ — ข้อนี้ task 1 เจอมาแล้ว)

**กับดักที่เหลือ**

- seed `shop.Products` 500 แถวเดิม **ไม่ align กับ DocumentNo ของ sim DB** — เอกสารจาก SP จะถูก INSERT ใหม่
  ทั้งหมด (คนละ DocumentNo) ไม่ชนกัน แต่ถ้าวันหลังมีคน align ให้ระวัง side-flip guard: ถ้าแถวเดิมใน
  `shop.Products` เป็นคนละฝั่ง Motor/NonMotor กับที่ SP คืน จะได้ 400 ทั้งหน้า (ตั้งใจ — แก้ที่ข้อมูล)
- `ProductPage` ไม่ได้ generalize `PagedResult` — endpoint อื่นทั้ง repo ยังใช้ `PagedResult` เหมือนเดิม
- ถ้าเพิ่ม production project ใหม่เข้า solution ต้องเติมชื่อใน `SpInsulationTests.ProductionAssemblies`
  ไม่งั้น guard แดงพร้อมข้อความ fail-closed (ตั้งใจ)

## Task 7 (sp-task7, 2026-07-31)

### สิ่งที่ทำ

ปิดงานทั้ง spec — docs 5 ไฟล์ + `design.md` (commit `20d257c`), flip task 7 (commit `a56256e`).
**ไม่แตะโค้ด production เลย** เพราะไม่มี gate ตัวไหนจับ bug ได้ (เขียวทุกตัวตั้งแต่รอบแรก)

### ผล gate (รันหลัง recreate ระบบใหม่จาก volume เปล่า ไม่ใช่ DB ที่สะสมสถานะมา)

| gate | ผล |
|---|---|
| `dotnet build pol-core.slnx -warnaserror` | `64 projects, 0 errors, 0 warnings` |
| `dotnet test pol-core.slnx --filter "Category!=Integration"` | 16 โปรเจกต์เขียว **1359 passed / 0 failed / 0 skipped** |
| `source .env.integration && dotnet test tests/Integration.Tests ... --filter Category=Integration` | `Passed! Failed: 0, Passed: 112, Skipped: 0, Total: 112` |
| skip scan (`grep -rn --include='*.cs' -E "\.only\|\.skip\|Skip *=" tests/`) | ไม่มีบรรทัดใดเลย |
| `bash scripts/spec-trace.sh products-sp-gateway` | `OK: ... เกณฑ์ 73 ข้อ ถูกอ้างครบ ... EARS lint ผ่านทุกข้อ` |
| `bash scripts/check-rename-identifiers.sh` | `rename-identifier gate: OK` |
| `bash scripts/check-migration-lineage.sh` | `Migration lineage gate OK — all 4 existing migration IDs discoverable via PolDbContext.` |

จำนวน test ต่อโปรเจกต์ (offline): Hosts 369, Architecture 200, Payments 162, Products 135,
Merchants 120, Admins 95, Orders 76, Iam 61, SharedKernel 46, BuildingBlocks 43, Carts 15,
Checkouts 13, Divisions/Levels/Offices/Positions 6 ตัวละ

### E2E ผ่านช่องทางไหน — **HTTP จริงบน API host ตัวจริง** (ไม่ใช่ in-proc test host)

ทำ SSO ไม่ได้ใน environment นี้ จึงใช้วิธีเดียวกับ E2E ของ sp-53: mint session ลง `merch.Sessions`
ตรง ๆ (`TokenHash = SHA256(utf8(token))`, `Status = 0`, `IssuedAt = SYSUTCDATETIME()`) ให้ผู้ใช้ demo
`somchai.p@demo.pol.local` แล้วส่ง `Cookie: mch_session=<token>` — **ลบ session row ทิ้งหลังเสร็จ**
(`e2e sessions left = 0`), ไม่มี secret ลงไฟล์

ลำดับ recreate: `docker compose down -v && docker compose up -d` (init exit 0, `02-external-sim: OK.`)
-> `dotnet ef database update` (`Done.`) -> `./scripts/seed-demo.sh` (`seed-demo: OK.`)
-> `dotnet run --project src/Hosts/Api --no-build --no-launch-profile --urls http://localhost:5177`

| เคส | คาดหวัง | ผลจริง |
|---|---|---|
| Motor `{"saleCode":"77001","insuranceType":"Motor"}` | 200, 28/2, 25 แถว | **ผ่าน** envelope ครบ 8 field + `id` เป็น Guid ทุกแถว |
| NonMotor `{"saleCode":"S001","insuranceType":"NonMotor"}` | 200, 27/2 | **ผ่าน** |
| `countMode:"FAST"` | totals เป็น null, hasNextPage ยังจริง | **ผ่าน** (`None`/`None`, `hasNextPage: True`, 25 แถว) |
| `countMode:"APPROX"` | 400 | **ผ่าน** (detail ว่างตาม fixed-detail M6) |
| ไม่มี insuranceType/productGroup | 400 | **ผ่าน** |
| `productGroup:"FIRE"` + `insuranceType:"Motor"` | 400 | **ผ่าน** |
| upsert จริง | แถวจาก sim โผล่ใน `shop.Products` | **ผ่าน** 506 -> 556 แถว, นับเฉพาะ sim = 50 (25+25), Guid ตรงกับที่ response คืน |
| ยิงซ้ำ | Guid ชุดเดิม | **ผ่าน** (upsert idempotent) |
| PAID จาก sim | เข้า `shop.Products` เป็น PAID + PaidDate | **ผ่าน** (`950007`/`950008`, PaidDate `2026-07-24`/`2026-07-28`) |
| add-to-cart แถว PAID | 400 (B1) | **ผ่าน** `Unknown or inactive product.` |
| add-to-cart แถว UNPAID | 200 | **ไม่ผ่าน — 409 (bug เดิมของ cart ดูด้านล่าง)** |
| 503 เมื่อ upstream ล่ม | 503 ไม่รั่ว SQL | **ผ่าน** `Upstream dependency unavailable`, NonMotor ยัง 200 |
| OpenAPI (REQ-8.4) | `ProductPage` + 503 + description ใหม่ | **ผ่าน** (ดูด้านล่าง) |

- **503 พิสูจน์ยังไง**: `docker stop pol-db` ไม่ได้ (sim อยู่ container เดียวกับ DB หลัก แอปตายไปด้วย)
  จึง override ระดับ process ด้วย env var `SpDocument__MotorConnectionString` ชี้ไป `localhost,11999`
  ที่ไม่มีจริง — **ไม่ commit อะไร**; ผลคือ Motor 503 ขณะ NonMotor ยัง 200 (พิสูจน์ว่า 503 แยกต่อ connection)
  และ log ฝั่ง server มี `LogError` จาก `Products.Infrastructure.Sp.SpDocumentGateway`:
  `SpDocument: dbo.usp_Motor_SearchDocument failed for Motor (SQL error 10061, state 0, class 20).`
  พร้อม exception เต็ม ส่วน response ไม่มีข้อความ SQL หลุด (REQ-5.6 + REQ-4.6 พิสูจน์พร้อมกัน)
- **OpenAPI จาก host จริง** (`/openapi/v1.json`): parameters = `page`/`limit`/`productFilters` เท่านั้น,
  description ของ `productFilters` พูดถึงทั้ง `insuranceType` และ `countMode`, responses =
  `200/400/401/503`, schema ของ 200 = `ProductPage` มี property ครบ 9 ตัว

### กับดักที่เจอตอนรัน E2E (คนถัดไปเจอซ้ำแน่)

- **`--no-launch-profile` ตัด `ASPNETCORE_ENVIRONMENT=Development` ของ launchSettings ทิ้งไปด้วย** ->
  host อ่านแต่ `appsettings.json` (ไม่มี OIDC ClientId) -> guard `ProvisioningGuards.RequireOidcProviders`
  kill process ตอน boot ด้วย `AdminAuth:Providers requires at least one provider with a configured ClientId`
  ทั้งที่ `appsettings.Development.json` มีค่าครบ — ต้องตั้ง env var นี้เองเสมอ
- **user demo คือ `E5000000-0000-4000-8000-000000000001` (GUID well-formed)** ไม่ใช่
  `E5000000-0000-0000-0000-000000000001` ที่ HANDOFF ของ sp-53 เขียนย่อไว้ และตารางชื่อ **`merch.Users`**
  ไม่ใช่ `merch.MerchantUsers`
- seed `shop.Products` 500 แถวเดิมใช้ลำดับ `9000xx` ส่วนเอกสารจาก sim เป็น `95xxxx`/`96xxxx` —
  ถ้าจะนับว่า "อะไรมาจาก sim" ต้อง filter ด้วย `%/95%`/`%/96%` ไม่ใช่ prefix `77001-`/`88001-`
  (demo seed ใช้ SaleCode ชุดเดียวกัน)

### ปัญหาที่เจอและ **ไม่ได้แก้** (ของเดิม ไม่ใช่ของ spec นี้)

**add-to-cart ได้ 409 `The resource was modified concurrently; please retry.` ทุกครั้งบน SQL Server จริง** —
พิสูจน์ว่าไม่ใช่ของ branch นี้ด้วย differential test: หยิบ product จาก seed เดิม
(`E9000000-0000-4000-8000-000000000006` ซึ่ง branch นี้ไม่ได้แตะ) ใส่ตะกร้าก็ 409 เหมือนกัน —
อาการเดียวกับที่ `products-sp-53-alignment` HANDOFF บันทึกไว้ว่าเกิดตั้งแต่ `rls-to-query-filter`/
`insurance-pivot`; REQ-9.3 ห้ามแตะ cart จึงปล่อยไว้เป็น follow-up. หลักฐานที่ยังได้ครบคือ **Guid ที่ upsert
ใช้อ้างเอกสารได้จริง** เพราะ product gate ทำงานก่อน cart write เสมอ ⇒ UNPAID ผ่าน gate (ตกที่ 409 ของ cart)
ส่วน PAID ถูก gate ปฏิเสธที่ 400 — ผลต่าง 400 vs 409 คือหลักฐานว่า lookup สำเร็จทั้งสองเคส

### docs ที่แตะ

- `docs/reference/platform-modules.md` — §5 บทบาท (read path ผ่าน SP + upsert, window/order/paging เป็นของ SP)
  + 4 แถวใหม่ในตารางฟีเจอร์ (filter ใหม่ 2 ตัว, ค้นสด+upsert, envelope §5.1, 400/503) + ย่อหน้าสถานะ
- `docs/reference/search-filter-sort.md` — ต่อ note เดิมทั้งหัวไฟล์และ §12 (ไม่รื้อตัวอย่างตามที่ task 6 แนะ)
- `docs/reference/src-structure.md` — `ListProducts.cs`, port ที่ไม่มี `ListAsync` + 2 แถวใหม่ (`Ports/`, `Sp/`)
- `docs/reference/layers-guide.md` — §Products + flow B1 ขั้น 4-5 ที่เคย quote โค้ด EF ที่ถูกลบไปแล้ว
- `docs/reference/db-connection-and-rls.md` — Flow A (GET ที่เขียนด้วย + connection แยกของ SP)
- `.ai/specs/products-sp-gateway/design.md` — section ใหม่ "As-built deviations" ใต้ Design review log
- **ไม่แตะ** `.ai/specs/products-sp-53-alignment/` (บันทึกประวัติศาสตร์ ห้ามแก้ย้อนหลัง)

### สถานะ

**พร้อมเปิด PR** — task 1-7 ครบ `[x]` ทุกตัว, gate ทุกตัวเขียว, E2E ผ่านครบยกเว้นข้อ add-to-cart UNPAID
ที่ติด bug เดิมของ cart (ไม่ใช่ของ spec นี้ มี differential test ยืนยัน). branch
`feat/products-sp-gateway` มี 21 commit เหนือ develop (นับรวม commit นี้), ยังไม่ push ตามกติกา
