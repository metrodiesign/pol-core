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
