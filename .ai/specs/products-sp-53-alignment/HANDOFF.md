# HANDOFF — products-sp-53-alignment

Rolling handoff. teammate แต่ละคน **อ่านไฟล์นี้ทั้งไฟล์ก่อนเริ่ม** แล้ว **ต่อท้าย section ของตัวเอง
ก่อนจบงาน** ห้ามลบ/แก้ section ของคนก่อน

## กติกาที่ใช้ร่วมกันทุก task

- branch: `feat/products-sp-53-alignment` (ห้าม push develop, ห้าม force push, ห้าม merge)
- plan ต้นทาง: `~/.claude/plans/src-modules-products-vast-cookie.md` — อ่านก่อนทุกคน
- เอกสารต้นเรื่อง: `docs/reference/vcentralpay-sp-quick-reference.pdf`
  (§2 input params หน้า 3, §5.1 pagination หน้า 6, §5.2 result set หน้า 6-7, §6 errors หน้า 8)
- canon ที่ต้องอ่าน: `.ai/shared/CODING_STANDARDS.md`, `.ai/shared/ARCHITECTURE.md`,
  `.ai/shared/LESSONS.md`, `.ai/shared/TASK_PROTOCOL.md`
- **build จะแดงระหว่าง T2-T4** (ตัดคอลัมน์แล้ว consumer ยังไม่ตาม) — ปกติ ห้ามไปแก้ไฟล์
  นอก task ของตัวเองเพื่อดับแดง ให้บันทึกไว้ใน section ตัวเองว่าอะไรยังแดง
  build จะกลับเขียวที่ T6 (tests) และ gate เต็มที่ T7
- ทุกคน commit งานตัวเองก่อนจบ (`git add` เฉพาะไฟล์ที่ตัวเองแตะ) message ตาม convention repo (EN)
- ห้าม `.only`/`.skip` ค้างใน test; ห้าม commit secret
- ห้ามแก้ migration/designer เก่า — `scripts/check-migration-lineage.sh` เฝ้าอยู่

## ลำดับ task

| T | ขอบเขต | สถานะ |
|---|---|---|
| T1 | spec artifacts (requirements/design/tasks) + PDF pointer | เสร็จ |
| T2 | Domain: `Product.cs`, `ProductInput.cs` | เสร็จ |
| T3 | Application: read model, `ProductFilterDto`, query, ลบ dead code | เสร็จ |
| T4 | Hosts + Repository: gate, currency boundary, ListAsync, ลบ `ProductSfs.cs` | รอ |
| T5 | EF config ×2 + migration ใหม่ + snapshot | รอ |
| T6 | seed-demo.sql + spec demo-seed-data REQ-5.4 | รอ |
| T7 | cap 25 ทั้ง repo + SFS docs/spec | รอ |
| T8 | tests ทั้งชุด → build+test เขียว | รอ |
| T9 | gate เต็ม + PR | รอ |

---

## T1 — spec artifacts (เสร็จ)

### ไฟล์ที่สร้าง / แก้

- `.ai/specs/products-sp-53-alignment/requirements.md` (REQ-1 ถึง REQ-12, 53 เกณฑ์, EARS)
- `.ai/specs/products-sp-53-alignment/design.md` (Field mapping, จุดแก้ 8 ชั้น, Traps 12 ข้อ, Verification, `## Requirement Traceability`)
- `.ai/specs/products-sp-53-alignment/tasks.md` (task 1-9 = T1-T9 หนึ่งต่อหนึ่ง)
- `docs/reference/vcentralpay-sp-quick-reference.pdf` เข้า git แล้ว (เอกสารต้นเรื่อง อ่านด้วย Read tool ได้ 8 หน้า)
- comment-only fix 4 ไฟล์: `Products.Domain/{DocumentType.cs,PaymentStatus.cs,ProductGroup.cs,Product.cs}` — ทุกจุดที่เคยเขียน "VCentralPay SP guide" ลอย ๆ ชี้ path จริงแล้ว

### เลข REQ ที่ T2-T9 ต้องอ้าง (บรรทัด `Satisfies:`)

| REQ | ครอบอะไร | task ที่ถือ |
|---|---|---|
| REQ-1 (1.1-1.7) | `Product` เป็น mirror §5.2: ลบ `BranchCode`/`IsActive`/`CreatedAt`, premium เป็น decimal, ลบ 5 currency column, rename 4 column, guard scale <= 2, `Create` ตัด `createdAt` | T2 |
| REQ-2 (2.1-2.4) | gate cart/checkout เป็น `PaymentStatus == UNPAID`, ลบ `Deactivate()`, `MarkPaid` เหลือ 2 field, seed แถวเดิมที่ `IsActive=0` -> PAID | T2 (2.2/2.3), T4 (2.1), T6 (2.4) |
| REQ-3 (3.1-3.6) | §2 input: `paymentStatus` default UNPAID + wire `UNPAID\|PAID\|ALL`, `saleCode` required, `@BranchCode` ไม่รองรับ, `Parse` blank -> throw, คงข้อที่ตรงแล้ว | T3, T4 (3.4) |
| REQ-4 (4.1, 4.2) | `@PageSize` cap 25 ทั้ง repo + docs SFS | T7 |
| REQ-5 (5.1, 5.2) | ทะเบียนรถเข้า predicate เฉพาะแถว Motor แบบ per-row | T4 |
| REQ-6 (6.1-6.4) | search window 6 เดือน / RENEWAL 2 เดือนบน `EndDate`, ผ่าน `IClock`, ค่าคงที่มีชื่อ | T4 |
| REQ-7 (7.1-7.5) | SFS teardown: เลิกรับ `filters`/`sort`/`search`, order `DocumentNo`, ลบ `ProductSfs.cs`, OpenAPI marker ใหม่, `ListProductsQuery` เลิก inherit `PagedQuery` | T3 (7.5), T4 (7.1-7.4) |
| REQ-8 (8.1-8.4) | `ProductListItem` = §5.2 32 field + `Id`, ลบ `ProductView`/`GetProductsQuery`/`ListByTenantAsync`, currency boundary จุดเดียว | T3, T4 (8.4) |
| REQ-9 (9.1-9.4) | error 50005 / 50007 / 50003-50008-50009 -> 400 ProblemDetails ผ่าน `ArgumentException` | T3 |
| REQ-10 (10.1-10.6) | EF config x2, migration ใหม่ + `Down()` ครบ, snapshot, seed-demo, spec demo-seed-data | T5 (10.1-10.4), T6 (10.5, 10.6) |
| REQ-11 (11.1-11.3) | comment ชี้ path จริง, PDF เข้า repo, docs/spec ที่อ้าง field ที่หายไป | T1 (11.1, 11.2), T7 (11.3) |
| REQ-12 (12.1-12.6) | build/test/gate/DB column/E2E | T8 (12.2), T9 (ที่เหลือ) |

### ผล gate

`bash scripts/spec-trace.sh products-sp-53-alignment` -> `OK: 'products-sp-53-alignment' เกณฑ์ 53 ข้อ
ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ` (real pass ไม่ใช่ skip)
`dotnet build src/Modules/Products/Products.Domain` -> 2 projects, 0 errors, 0 warnings

### สิ่งที่คนถัดไปต้องรู้ / กับดักที่เจอ

1. **spec-trace เข้มกว่าที่คิด** — ไม่ได้เช็คแค่ heading `## REQ-N:`: (ก) เกณฑ์ต้องเป็นบรรทัด
   `- N.M <ข้อความ>` เท่านั้น (`- N.M. ` มีจุดเกิน = ถูกข้ามเงียบ แค่เตือน stderr);
   (ข) ทุกเกณฑ์ต้องมี `THE SYSTEM SHALL` / `WHEN` / `WHILE` / `WHERE` / `IF...THEN`;
   (ค) design.md **ต้องมี section ชื่อ `## Requirement Traceability` เป๊ะ ๆ** ถ้าไม่มี ทุกเกณฑ์นับว่าไม่ถูกอ้าง;
   (ง) tasks.md นับเฉพาะ reference บนบรรทัด `Satisfies:` — เขียน REQ ที่อื่นในบล็อก task ไม่นับ
   ตอนเพิ่ม/แก้เกณฑ์ ต้องอัปเดตทั้งตาราง traceability ใน design.md และบรรทัด `Satisfies:` มิฉะนั้น gate แดง
2. **task gate ต้อง flip + Evidence ใน Edit เดียว** — เจอจริงใน T1: แก้ `[ ]` -> `[x]` แล้วค่อยเติม
   Evidence ทีหลัง โดน `.claude/hooks/task-gate.sh` block พร้อมข้อความ "ขาด Evidence (per-task)"
   ทางแก้ = Edit ก้อนเดียวที่มีทั้ง `- [x]` และ `Evidence:` (Evidence ของ task อื่นใช้แทนกันไม่ได้)
3. **ชื่อคอลัมน์ไม่ตรงชื่อ CLR ใน premium breakdown** — บรีฟบอกว่าลบ `CommissionCurrency` ซึ่งถูก
   แต่ CLR property ชื่อ `CommissionAmountAmount`/`CommissionAmountCurrency` แล้ว map ด้วย
   `HasColumnName("CommissionAmount")`/`HasColumnName("CommissionCurrency")` (ยืนยันที่
   `Persistence.MerchantRuntime/Products/ProductConfiguration.cs:64-65`) ⇒ **คอลัมน์
   `CommissionAmount` ชื่อตรง §5.2 อยู่แล้ว ไม่ต้อง rename** แต่ CLR ต้องเปลี่ยนเป็น `CommissionAmount`
   (decimal?) — ตอน grep residue ต้อง grep ทั้งชื่อ CLR และชื่อคอลัมน์ (ตาราง Field mapping ใน design.md
   ระบุครบทั้งสองแกน ใช้ตารางนั้นเป็นหลัก ไม่ใช่บรีฟ)
4. rename 4 คอลัมน์ที่ต้องทำจริง = `TotalPremiumAmount`->`TotalPremium`, `NetPremiumAmount`->`NetPremium`,
   `StampAmount`->`Stamp`, `TaxVatAmount`->`TaxVat`; DROP 8 = `BranchCode`, `IsActive`, `CreatedAt`,
   `TotalPremiumCurrency`, `NetPremiumCurrency`, `StampCurrency`, `TaxVatCurrency`, `CommissionCurrency`
5. **comment ใน `Product.cs` ยังพูดถึง Money/complex type/decimal(19,4)** — T1 แก้แค่ path pointer
   ตามขอบเขต; T2 ต้องแก้เนื้อ comment นั้นให้ตรงของจริงหลังเปลี่ยนเป็น `decimal(19,2)`
6. XML doc ที่ยังผิดและ T3 ต้องแก้: `ListProducts.cs:12` เขียนว่า `ProductListItem` เป็น
   "Deliberately a slim subset" (จะกลายเป็น 32 field) และ `:19-24` เขียนว่า filter surface เป็น
   "Optional" + อ้างว่า BranchCode/SaleCode "are an authorization scope — never client input"
   (ขัดกับ REQ-3.3 ที่ user ตัดสินให้รับ `SaleCode` จาก client)
7. เอกสาร PDF อ่านด้วย Read tool ได้ตรง ๆ (`pages: "1-8"`) — §2 หน้า 3, §3 หน้า 4, §4 หน้า 5,
   §5.1/§5.2 หน้า 6-7, §6 หน้า 8

commit ของ T1: `6849f28` (spec 3 ไฟล์ + HANDOFF + PDF + comment fix 4 ไฟล์ อยู่ใน commit นี้ทั้งหมด)

---

## T2 — Domain (เสร็จ)

แตะ 2 ไฟล์เท่านั้น: `src/Modules/Products/Products.Domain/{Product.cs,ProductInput.cs}`

### signature สุดท้าย

`Product.Create(ProductInput input)` — param `createdAt` หายไปแล้ว (คนเรียกเดิม
`Product.Create(input, clock.UtcNow)` ต้องตัด argument ที่สอง)

`ProductInput` ลำดับ parameter ครบ (T4/`CreateProductRequest` ใช้ลำดับนี้):

```csharp
public sealed record ProductInput(
    Guid MerchantId,
    ProductGroup ProductGroup,
    DocumentType DocumentType,
    string DocumentNo,
    string SaleCode,
    decimal TotalPremium,
    string? PolicyYear = null,
    string? ReferenceBranch = null,
    string? ReferencePre = null,
    string? PolicySequenceNo = null,
    string? ReferenceYear = null,
    string? ReferenceNo = null,
    string? PolicyBranch = null,
    string? PolicyType = null,
    string? SaleFullName = null,
    string? BrokerCode = null,
    string? BrokerName = null,
    string? PolicyNumber = null,
    string? ApplicationNumber = null,
    string? PreviousPolicyNumber = null,
    string? EndorsementNumber = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? ShowName = null,
    string? LicensePlateNumber = null,
    decimal? NetPremium = null,
    decimal? Stamp = null,
    decimal? TaxVat = null,
    decimal? CommissionAmount = null,
    decimal? CommissionPercent = null);
```

ตำแหน่งที่ 5 เดิมคือ `string BranchCode` — ถูกตัดออก ทำให้ทุก positional argument
ตั้งแต่ตัวที่ 5 เลื่อนซ้ายหนึ่งช่อง (เงียบ ๆ ไม่ error ถ้าเผอิญ type ตรง) ระวังจุดนี้ตอนแก้ caller

`Product.MarkPaid(DateTime paidDate)` — เหลือ `PaymentStatus = PAID` + `PaidDate` (ไม่แตะอะไรอีก)

### ชื่อ property สุดท้ายบน `Product` ที่เปลี่ยน

| เดิม | ใหม่ |
|---|---|
| `Money TotalPremium` | `decimal TotalPremium` |
| `decimal? NetPremiumAmount` + `string? NetPremiumCurrency` + computed `Money? NetPremium` | `decimal? NetPremium` |
| `decimal? StampAmount` + `string? StampCurrency` + computed `Money? Stamp` | `decimal? Stamp` |
| `decimal? TaxVatAmount` + `string? TaxVatCurrency` + computed `Money? TaxVat` | `decimal? TaxVat` |
| `decimal? CommissionAmountAmount` + `string? CommissionAmountCurrency` + computed `Money? CommissionAmount` | `decimal? CommissionAmount` |
| `string BranchCode`, `bool IsActive`, `DateTime CreatedAt` | ลบทิ้ง |
| `Deactivate()`, `RequireThb(Money?, string)` | ลบทิ้ง |

คงเดิม: `decimal? CommissionPercent`, `PaymentStatus`, `DateTime? PaidDate`,
computed `InsuranceType` (ยัง `builder.Ignore`), `Id`, `MerchantId` และ field string/date อื่นทั้งหมด

### วิธีตรวจ scale (REQ-1.5)

helper ใหม่แทนที่ `RequireThb` (สองตัว overload กัน — nullable ส่งต่อให้ตัว non-nullable, `null` ผ่าน):

```csharp
private static decimal RequireMoney(decimal value, string name)
{
    if (value < 0)
        throw new ArgumentException($"{name} must not be negative.", name);
    if (decimal.Round(value, 2) != value)
        throw new ArgumentException($"{name} must not have more than 2 decimal places.", name);
    return value;
}

private static decimal? RequireMoney(decimal? value, string name) =>
    value is { } v ? RequireMoney(v, name) : null;
```

เรียกใน object initializer ของ `Create` ทั้ง 5 ค่า (`TotalPremium` + breakdown 4 ตัว) —
ไม่ใช้ float/double เลย. พฤติกรรมที่ T8 เขียน test ได้ตรง ๆ:

- `100.5m`, `100.50m`, `100m` ผ่าน (`==` ของ decimal ไม่สนใจ trailing zero: `1.50m == 1.5m`)
- `100.005m`, `100.001m` throw (`ArgumentException`, paramName = ชื่อ field)
- ค่าติดลบของ breakdown throw (เดิม `Money.Of` กันให้); `TotalPremium <= 0` ยัง throw ก่อนถึง guard นี้
  ด้วยข้อความเดิม `"TotalPremium must be greater than zero."`

invariant ที่คงไว้ครบ: `TotalPremium <= 0`, `StartDate > EndDate`, `Enum.IsDefined` ทั้ง
`ProductGroup`/`DocumentType`, กฎ CMI + APPLICATION, `Required`/`Optional` length trim ทุกตัว
(`Required(input.BranchCode, 3, ...)` หายไปพร้อม field)

### comment ที่แก้เนื้อ (ต่อจาก T1 กับดักข้อ 5)

- class doc ของ `Product`: เลิกพูดถึง EF complex type / `decimal(19,4)` / "nullable Amount+Currency
  pair pattern" -> ระบุว่าเป็น `decimal(19,2)` ไม่มีคอลัมน์ currency, currency mint ที่ cart boundary,
  และ check ของ `Money.Of` ย้ายมาอยู่ใน `Create`
- doc ของ `TotalPremium`: "decimal(19,2) THB"
- `ProductInput` class doc: ชี้ path PDF จริง (เดิมเขียน "VCentralPay SP guide" ลอย ๆ) + ระบุว่า premium
  เป็น THB decimal ไม่เกิน 2 ตำแหน่ง
- ลบ `using SharedKernel;` ออกจาก `ProductInput.cs` (ไม่ใช้ `Money` แล้ว) — `Product.cs` ยังต้องมี
  เพราะ `AggregateRoot<Guid>` อยู่ `src/SharedKernel`

### build ที่แดงอยู่ตอนนี้ (คาดไว้ ห้ามดับด้วยการแก้ไฟล์นอก task)

`dotnet build src/Modules/Products/Products.Domain` -> **0 error / 0 warning** (2 projects)

`dotnet build pol-core.slnx` หยุดที่ `Products.Application` (18 error) — โปรเจกต์ถัดจากนั้นยังไม่ได้ compile
จึงยังไม่รู้ error จริงทั้งหมด รายการที่ยืนยันแล้ว:

| ไฟล์ | error | สาเหตุ | เจ้าของ |
|---|---|---|---|
| `Products.Application/ProductView.cs:51` | CS1061 | `Product.BranchCode` ไม่มีแล้ว | T3 (ไฟล์นี้ถูกลบทั้งไฟล์ตาม REQ-8.2) |
| `Products.Application/ProductView.cs:54` | CS1503 x5 (arg 27-32) | `decimal`/`decimal?` -> `Money`/`Money?` | T3 |
| `Products.Application/ProductView.cs:55` | CS1061 x2 | `Product.IsActive`, `Product.CreatedAt` ไม่มีแล้ว | T3 |
| `Products.Application/CreateProductCommand.cs:31` | CS1501 | เรียก `Product.Create` ด้วย 2 argument | T3 |

ที่ยังไม่ compile แต่แน่นอนว่าจะแดง (จาก grep — เตรียมไว้ให้ T4/T5/T8):

- `src/Hosts/Api/Program.cs` — cart boundary + gate `IsActive` (T4) และเป็นจุด mint `Money.Of(product.TotalPremium, "THB")`
  ซึ่งตอนนี้ compile ผ่าน type ได้แล้ว (`decimal` -> `Money.Of`) แต่ gate ยังอ้าง `IsActive`
- `src/.../Persistence.MerchantRuntime/Products/{ProductSfs.cs,ProductConfiguration.cs,ProductRepository.cs}` (T4/T5)
- `src/.../Products.Infrastructure/.../ProductConfiguration.cs` (T5)
- `src/.../Products.Application/ListProducts.cs` (T3)
- tests: `tests/Products.Tests/{ProductTests.cs (25 จุด),DocumentPaidOnOrderPaidConsumerTests.cs}`,
  `tests/Architecture.Tests/{ProductSfsTests.cs,ProductRepositoryListTests.cs,MoneyColumnMappingTests.cs,WriteFloorTests.cs,ReadFloorTests.cs}`,
  `tests/Hosts.Tests/{ProductInsuranceFieldsRoundTripTests.cs,InsuranceCheckoutEndToEndTests.cs}` (T8)

หมายเหตุ: `PolDbContextModelSnapshot.cs` + designer เก่า ๆ ยังอ้างชื่อคอลัมน์เดิม — **ห้ามแก้ designer เก่า**
snapshot ให้ regen ด้วย `dotnet ef` (T5, REQ-10.4)

### กับดักที่เจอ

1. **task-gate ยิงจริงตามที่ T1 เตือน** — flip `[ ]` -> `[x]` ก่อนแล้วเติม `Evidence:` ทีหลังโดน block
   ("ขาด Evidence (per-task)"); ที่รอดคือ Edit ถัดไปที่เติม `Evidence:` ต่อท้ายบรรทัด `Satisfies:`
   ในบล็อกเดียวกัน — ทางที่ปลอดภัยกว่าคือทำ flip + Evidence ใน Edit เดียวตั้งแต่แรก
2. **`Done` clause ของ task 2 ใน `tasks.md` สั่งรัน `tests/Products.Tests`** ซึ่งขัดกับขอบเขต T2
   (2 ไฟล์ domain) และกับตาราง `HANDOFF.md` ที่ยก tests ให้ T8 — บันทึกเป็น deviation ใน Evidence แล้ว
   ไม่ได้แตะ `tests/`
3. **`ProductInput` ตัด parameter กลางลิสต์** = positional call ทุกจุดเลื่อน โดยที่ compiler ไม่ช่วย
   ถ้า type ข้างเคียงตรงกัน (ตำแหน่ง 5 เดิม `BranchCode`, 6 `SaleCode` เป็น `string` ทั้งคู่) —
   ตอนแก้ caller ต้องอ่านชื่อ argument ไม่ใช่แค่ให้ build ผ่าน
4. **ชื่อ CLR `CommissionAmountAmount` -> `CommissionAmount`** ตามกับดัก T1 ข้อ 3 — คอลัมน์ชื่อ
   `CommissionAmount` อยู่แล้ว ดังนั้น T5 ต้อง **ไม่** `RenameColumn` ตัวนี้ แค่เปลี่ยน precision + DROP
   `CommissionCurrency`
5. `rtk` ย่อ output ของ `dotnet build` เหลือบรรทัดเดียว — ตอนต้องอ่าน error CS จริงต้องใช้
   `rtk proxy dotnet build ...`

commit ของ T2: `5159286`

---

## T3 — Application (เสร็จ)

แตะ 4 ไฟล์ + ลบ 2 ไฟล์ ใน `src/Modules/Products/Products.Application/` เท่านั้น:
แก้ `ListProducts.cs`, `GetProductById.cs`, `IProductRepository.cs`, `CreateProductCommand.cs`;
ลบ `ProductView.cs`, `GetProductsQuery.cs`

### `ProductListItem` — field list สุดท้าย (T4 เขียน projection ตามลำดับนี้เป๊ะ)

ลำดับ = ลำดับตาราง §5.2 หน้า 6-7 ตรง ๆ โดยแทรก `Id` ไว้หน้าสุด และ **ไม่มี** `InsuranceType`
ใน constructor (เป็น computed) ⇒ ctor มี 32 parameter

| # | parameter | type | หมายเหตุ |
|---|---|---|---|
| 1 | `Id` | `Guid` | technical key (นอก §5.2) |
| 2 | `ProductGroup` | `ProductGroup` | = §5.2 `SourceSystem` (คงชื่อ §2 `@ProductGroup`) |
| 3 | `DocumentType` | `DocumentType` | enum |
| 4 | `DocumentNo` | `string` | non-null (repo เป็นเจ้าของข้อมูล) |
| 5 | `PolicyYear` | `string?` | |
| 6 | `ReferenceBranch` | `string?` | |
| 7 | `ReferencePre` | `string?` | |
| 8 | `PolicySequenceNo` | `string?` | |
| 9 | `ReferenceYear` | `string?` | |
| 10 | `ReferenceNo` | `string?` | |
| 11 | `PolicyBranch` | `string?` | |
| 12 | `PolicyType` | `string?` | |
| 13 | `SaleCode` | `string` | non-null |
| 14 | `SaleFullName` | `string?` | |
| 15 | `BrokerCode` | `string?` | |
| 16 | `BrokerName` | `string?` | |
| 17 | `PolicyNumber` | `string?` | |
| 18 | `ApplicationNumber` | `string?` | |
| 19 | `PreviousPolicyNumber` | `string?` | §5.2 เขียน `previousPolicyNumber` (p เล็ก) — CLR ใช้ PascalCase |
| 20 | `EndorsementNumber` | `string?` | |
| 21 | `StartDate` | `DateTime?` | |
| 22 | `EndDate` | `DateTime?` | |
| 23 | `ShowName` | `string?` | |
| 24 | `NetPremium` | `decimal?` | |
| 25 | `Stamp` | `decimal?` | |
| 26 | `TaxVat` | `decimal?` | |
| 27 | `TotalPremium` | `decimal` | non-null (entity เป็น non-null) |
| 28 | `CommissionPercent` | `decimal?` | |
| 29 | `CommissionAmount` | `decimal?` | |
| 30 | `PaidDate` | `DateTime?` | |
| 31 | `LicensePlateNumber` | `string?` | |
| 32 | `PaymentStatus` | `PaymentStatus` | enum, non-null |

**`InsuranceType` — ตัดสินให้เป็น computed property บน record** (ไม่ให้ repository project):
`ProductGroup is CMI or VMI ? Motor : NonMotor` เหมือน `Product.InsuranceType` เป๊ะ
เหตุผล: `InsuranceType` เป็น `builder.Ignore` แปลเป็น SQL ไม่ได้ ถ้าเป็น ctor param จะบังคับให้
projection ต้องคำนวณใน expression tree เอง (ซ้ำ logic 3 ที่) ⇒ **T4 ไม่ต้องส่งค่านี้**
(ยังคืนให้ client ตาม §5.2 เพราะ System.Text.Json serialize computed property ให้อยู่แล้ว)

**`MerchantId` ไม่มีใน `ProductListItem` แล้ว** (เดิมเป็น parameter ที่ 2) — ไม่ใช่ field §5.2
และไม่มี consumer จริง (grep แล้ว: มีแต่ `ProductRepository.ListAsync` projection กับ
`Program.cs:655` `.Produces<>`); ตัว filter merchant คือ query filter + `GetProductByIdHandler`
⇒ **T4 ต้องลบ argument นี้ออกจาก projection**

มี factory `ProductListItem.From(Product p)` แทน `ProductView.From` — `GetProductByIdHandler` ใช้ตัวนี้

### `IProductRepository` — signature สุดท้าย

```csharp
void Add(Product product);
Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken);
Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken);
```

`ListByTenantAsync` ถูกลบ (implementation ที่ `ProductRepository.cs:26-29` เป็นของ T4;
fake ใน `tests/Products.Tests/DocumentPaidOnOrderPaidConsumerTests.cs:76` เป็นของ T8)
`ListAsync` signature **ไม่เปลี่ยน** แต่ XML doc เลิกพูดถึง SFS/RLS แล้ว

### `ListProductsQuery` — signature สุดท้าย

```csharp
public sealed record ListProductsQuery : IQuery<PagedResult<ProductListItem>>, IMerchantScoped
{
    public required Guid MerchantId { get; init; }
    public required ProductFilterDto ProductFilters { get; init; }   // required + non-nullable
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = 25;
}
```

เลิก inherit `PagedQuery` ⇒ `Filters`/`Sort`/`Search` **หายไปจาก type นี้** (T4 ต้องเลิกอ่าน 3 ตัวนี้
ใน `ProductRepository`) และ `ProductFilters` เป็น `required` ⇒ ทุก object initializer ต้องส่งค่า

### `ProductFilterDto` — พฤติกรรมครบทุก path (T8 เขียน test ตามนี้ได้ตรง ๆ)

property: `SaleCode` (`[Required][MaxLength(20)]`, `string?` ใน declaration แต่ `Parse` การันตีว่า
มีค่า trim แล้วไม่ว่าง), `SearchText`(100), `InsuredName`(200), `PolicyNo`(30), `ApplicationNo`(30),
`DocumentType?`, `ProductGroup?`, **`PaymentStatus` เป็น `string?`**, `CoverageStartFrom/To`,
`CoverageEndFrom/To` (`DateOnly?`), `PaidDateFrom/To` (`DateTime?`)

`Parse(string? raw)` เปลี่ยน return type จาก `ProductFilterDto?` -> **`ProductFilterDto`** (non-nullable)
ลำดับการตรวจใน `Parse`:

1. `raw` null/blank -> `ArgumentException` อ้าง 50005 (เดิม `return null`) — **breaking**: T4 เลิกจัดการ null ได้
2. deserialize ล้ม -> `ArgumentException("Malformed productFilters.")`
3. `dto is null` (literal `null` ใน JSON) -> `ArgumentException` อ้าง 50005
4. `SaleCode?.Trim()` ว่าง/หาย -> `ArgumentException("SaleCode is required (SP error 50005).")`
   **ต้องมาก่อน `Validator`** ไม่งั้น `[Required]` ยิงข้อความ generic ทับ ทำให้ไม่อ้าง 50005;
   แล้ว `dto = dto with { SaleCode = saleCode }` ⇒ ค่าที่ไปถึง repository ถูก trim แล้ว
5. `_ = dto.PaymentStatusFilter;` — บังคับอ่านเพื่อให้ 50007 เกิดที่ boundary ไม่ใช่กลาง query
6. `Validator.TryValidateObject(validateAllProperties: true)` -> `ArgumentException("Invalid productFilters.")`
   (ครอบ `MaxLength` ทุกตัว รวม `SaleCode` > 20)
7. cross-field เดิม 3 ข้อ: 50003 (`PaidDateFrom > PaidDateTo`), 50008 (coverage start), 50009 (coverage end)

`PaymentStatusFilter` (computed, `Products.Domain.PaymentStatus?`) — **T4 ต้องใช้ตัวนี้ ห้ามใช้
`PaymentStatus` ตรง ๆ**:

| wire `paymentStatus` | `PaymentStatusFilter` | ความหมาย |
|---|---|---|
| absent (`null`) | `UNPAID` | §2 default (เดิม absent = ALL) |
| `"UNPAID"` | `UNPAID` | |
| `"PAID"` | `PAID` | |
| `"ALL"` | `null` | ไม่กรอง |
| `"unpaid"` / `"NOPE"` / `""` | throw 50007 | case-sensitive (`ignoreCase: false`) |

`@BranchCode` **ไม่มี property** — ถ้า client ส่ง `"branchCode": "001"` มาใน JSON จะถูก **ignore เงียบ ๆ**
(`JsonSerializerDefaults.Web` ไม่ error กับ unknown member; ยืนยันด้วย harness แล้ว) ไม่ใช่ 400
ถ้าภายหลังต้องการให้ 400 ต้องตั้ง `UnmappedMemberHandling.Disallow` — จงใจไม่ทำในสเปกนี้

error ทุกตัวเป็น `ArgumentException` -> `ProblemDetailsExceptionHandler` = 400
**ห้ามเปลี่ยนเป็น `BadHttpRequestException`** (inherit `IOException` -> กลายเป็น 500)

### `CreateProductCommand.cs`

`Product.Create(command.Input)` (ตัด `_clock.UtcNow`) และ **ถอน `IClock` ออกจาก
`CreateProductHandler` ทั้ง field + ctor parameter** — DI ยัง register `IClock` ให้โมดูลอื่นอยู่
ไม่ต้องแก้ registration

### XML doc ที่แก้ (ตามกับดัก T1 ข้อ 6)

- `ProductListItem`: เลิกเขียน "Deliberately a slim subset" -> ระบุว่าเป็น mirror §5.2 32 field + `Id`
  พร้อมรายการ deviation ของ type (non-null 4 ตัว, enum vs string, `SourceSystem` = `ProductGroup`,
  ไม่มี `MerchantId`)
- `ProductFilterDto`: เลิกเขียน "Optional" (บังคับแล้ว) และเลิกอ้างว่า BranchCode/SaleCode
  "are an authorization scope — never client input" -> เขียนกำกับตรง ๆ ว่า `@SaleCode` รับจาก client
  **ขัด §1.1 ของเอกสารโดยรู้ตัว** (user ตัดสิน, floor จริงคือ `MerchantId`) และ `@BranchCode` ไม่รองรับ
- `ListProductsQuery`: ระบุเหตุผลที่ **ไม่** inherit `PagedQuery`

### build ที่ยังแดง (คาดไว้ ห้ามดับด้วยการแก้ไฟล์นอก task)

`dotnet build src/Modules/Products/Products.Application` -> **0 error / 0 warning** (5 projects)

`dotnet build pol-core.slnx` -> 126 error CS ใน 4 ไฟล์ (tests ยังไม่ได้ compile จึงยังไม่นับ):

| ไฟล์ | จำนวน | สาเหตุ | เจ้าของ |
|---|---|---|---|
| `Persistence.MerchantRuntime/Products/ProductSfs.cs` | 46 | อ้าง field ที่ถูกลบทั้งไฟล์ | T4 (ลบไฟล์นี้ทิ้ง) |
| `Persistence.MerchantRuntime/Products/ProductConfiguration.cs` | 32 | `BranchCode`/`IsActive`/`CreatedAt`/`*Amount`/`*Currency`/`ComplexProperty` | T5 |
| `Products.Infrastructure/ProductConfiguration.cs` | 32 | เหมือนกันเป๊ะ (mirror) | T5 |
| `Persistence.MerchantRuntime/Products/ProductRepository.cs` | 16 | `ListByTenantAsync` + `CreatedAt`, `query.Search/Filters/Sort` ไม่มีแล้ว, `p.PaymentStatus == query.ProductFilters.PaymentStatus` (CS0019: enum == string) ต้องเป็น `PaymentStatusFilter`, `new ProductListItem(...)` ขาด argument (CS7036) | T4 |

`src/Hosts/Api` ยังไม่ถูก compile (Persistence ล้มก่อน) — ที่แน่นอนว่าจะแดง/ต้องแก้ (T4):
gate `!product.IsActive` ที่ `Program.cs:679` + `:776`, `Money.Of(product.TotalPremium, "THB")` ที่ `:683`,
`ProductFilterDto.Parse` ที่เลิกคืน null, `ListProductsQuery` ที่ต้องส่ง `ProductFilters` (required),
`CreateProductRequest` (`:2204-2234`); tests เป็นของ T8 (รายการเดิมใน section T2 ยังใช้ได้)

### กับดักที่เจอ

1. **`PaymentStatus` เป็นชื่อ property แล้วชนชื่อ type** — ใน record ที่มี `public string? PaymentStatus`
   คำว่า `PaymentStatus` ใน body จะ resolve เป็น property ไม่ใช่ enum ⇒ ใส่
   `using DomainPaymentStatus = Products.Domain.PaymentStatus;` แล้วใช้ alias ทุกจุด
   (`DocumentType`/`ProductGroup` ที่ชื่อชนกันแบบเดียวกันรอดเพราะใช้แค่ในบรรทัด declaration)
2. **`[Required]` ยิงก่อนแล้วกลืนเลข error** — ต้องเช็ค `SaleCode` เอง **ก่อน** `Validator` ไม่งั้น
   REQ-9.1 (ต้องอ้าง 50005) ไม่ผ่าน; `[Required]` ยังเก็บไว้ในฐานะ backstop + เอกสาร
3. **`Parse` คืน non-nullable แล้ว** — caller ที่เคยเขียน `?? new ProductFilterDto()` หรือเช็ค null
   จะเป็น dead code / warning ตอน T4 แก้ `Program.cs`
4. **task gate ยิงซ้ำรอบที่ 3** — flip `[ ]` -> `[x]` ก่อนแล้วเติม Evidence ทีหลังถูก block จริง
   (แต่ Edit แรกเขียนไฟล์สำเร็จไปแล้ว ต้องรีบเติม Evidence ใน Edit ถัดไป) — T4-T9 ทำ
   flip + Evidence ใน Edit เดียวตั้งแต่แรก
5. **`tests/` อยู่นอกขอบเขต** แต่ Done clause ของ task 3 สั่งรัน `ProductFilterDtoTests.cs` —
   บันทึกเป็น deviation แล้วตรวจด้วย console harness ชั่วคราวใน scratchpad (referencing
   `Products.Application.csproj`) 15 เคสผ่านหมด: 50005 x4, 50007 x3, 50003/50008/50009,
   MaxLength 21 ตัวอักษร, absent=UNPAID, ALL=null, PAID=PAID, trim `"  S1  "` -> `"S1"`,
   `branchCode` ถูก ignore — **T8 ยกเคสชุดนี้ไปเขียนเป็น xunit ได้ตรง ๆ**

commit ของ T3: `10e63ee`
