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
| T4 | Hosts + Repository: gate, currency boundary, ListAsync, ลบ `ProductSfs.cs` | เสร็จ |
| T5 | EF config ×2 + migration ใหม่ + snapshot | เสร็จ |
| T6 | seed-demo.sql + spec demo-seed-data REQ-5.4 | เสร็จ |
| T7 | cap 25 ทั้ง repo + SFS docs/spec | เสร็จ |
| T8 | tests ทั้งชุด → build+test เขียว | เสร็จ |
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

---

## T4 — Hosts + Repository (เสร็จ)

แตะ 4 ไฟล์ + ลบ 1 ไฟล์:
`src/Hosts/Api/{Program.cs,SfsQueryParser.cs,SfsOpenApi.cs}`,
`src/Persistence/Persistence.MerchantRuntime/Products/ProductRepository.cs`;
ลบ `src/Persistence/Persistence.MerchantRuntime/Products/ProductSfs.cs`

### `ProductRepository.ListAsync` — ลำดับ clause สุดท้าย (T8 เขียน test ตามนี้ได้ตรง ๆ)

```csharp
var pf = query.ProductFilters;                       // non-nullable แล้ว ไม่มี if null

src = _db.Set<Product>().AsNoTracking()
    .Where(p => p.MerchantId == query.MerchantId)     // floor (defence-in-depth)
    .Where(p => p.SaleCode == pf.SaleCode);           // exact match, ค่าที่ Parse trim มาแล้ว

var today = _clock.UtcNow.Date;                       // IClock injected
src = src.Where(p =>
    (p.DocumentType == DocumentType.RENEWAL
        && p.EndDate >= today && p.EndDate < today.AddMonths(2))
    || (p.DocumentType != DocumentType.RENEWAL
        && p.StartDate >= today.AddMonths(-6)));

if (pf.PaymentStatusFilter is { } paymentStatus)      // null (wire ALL) = ไม่กรอง
    src = src.Where(p => p.PaymentStatus == paymentStatus);
// ... SearchText / InsuredName / PolicyNo / ApplicationNo / DocumentType / ProductGroup
// ... coverage 4 + PaidDate 2 (ตรรกะเดิมทั้งหมด ไม่เปลี่ยน)

long total = await src.LongCountAsync(ct);            // หลัง filter ก่อน paging
int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
var items = await src.OrderBy(p => p.DocumentNo).Skip(skip).Take(query.Limit)
    .Select(p => new ProductListItem(/* 32 arg */)).ToListAsync(ct);
```

ค่าคงที่ 2 ตัวเป็น `private const int` บน class: `SearchWindowMonths = 6`, `RenewalWindowMonths = 2`
(REQ-6.4 — กลับทิศ RENEWAL ได้ด้วยการแก้ clause เดียว)

### search window — แถวไหนเข้า/ไม่เข้า (สมมติ `today` = 2026-07-30)

| แถว | `DocumentType` | `StartDate` | `EndDate` | เข้า? | เหตุผล |
|---|---|---|---|---|---|
| A | `POLICY` | 2026-07-01 | any | เข้า | `StartDate >= 2026-01-30` |
| B | `POLICY` | 2026-01-30 | any | เข้า | ขอบล่าง inclusive (`>=` วันพอดี) |
| C | `POLICY` | 2026-01-29 | any | ไม่เข้า | เก่ากว่า 6 เดือน |
| D | `POLICY` | 2027-01-01 | any | เข้า | **ไม่มีขอบบน** — rule ทั่วไปกำหนดแค่ "ย้อนหลังไม่เกิน 6 เดือน" |
| E | `POLICY` | NULL | any | ไม่เข้า | `NULL >= x` = UNKNOWN (ผลข้างเคียงที่ยอมรับ) |
| F | `RENEWAL` | any | 2026-07-30 | เข้า | ขอบล่าง = today inclusive |
| G | `RENEWAL` | any | 2026-09-29 | เข้า | < today+2 เดือน |
| H | `RENEWAL` | any | 2026-09-30 | ไม่เข้า | ขอบบน half-open (`today.AddMonths(2)` = 2026-09-30) |
| I | `RENEWAL` | any | 2026-07-29 | ไม่เข้า | หมดอายุแล้ว (window มองไปข้างหน้า) |
| J | `RENEWAL` | 2026-01-01 (เก่า) | 2026-08-15 | เข้า | RENEWAL ไม่สน `StartDate` เลย |
| K | `RENEWAL` | any | NULL | ไม่เข้า | NULL semantics |

หมายเหตุ: window ถูก AND **เสมอ** ไม่ว่า client ส่ง filter อะไรมา — เป็น floor ไม่ใช่ option
`today` = `_clock.UtcNow.Date` (เวลา 00:00:00) เทียบกับคอลัมน์ `datetime2(0)`

### Motor gate ของทะเบียนรถ (REQ-5)

`LicensePlateNumber` เป็น **term ที่ 5** ใน `||` chain ของ `SearchText` โดยครอบด้วย per-row gate:

```csharp
|| ((p.ProductGroup == ProductGroup.CMI || p.ProductGroup == ProductGroup.VMI)
    && p.LicensePlateNumber != null && EF.Functions.Like(p.LicensePlateNumber, pattern, "\\"))
```

term ที่ 1-4 (`DocumentNo`, `PolicyNumber`, `ApplicationNumber`, `EndorsementNumber`) **ไม่มี gate**
= §3 กับ §4 ตรงกันในสี่ตัวนี้ (เลขกรมธรรม์/ใบคำขอ/สลักหลัง/ใบเตือน)
ตัดสิน: gate เป็น per-row เพราะ `InsuranceType` เป็น `builder.Ignore` แปลเป็น SQL ไม่ได้ และ per-request
(อ่าน `pf.ProductGroup`) จะผิดตอน client ไม่ส่ง `productGroup` — แถว FIRE/MISC จะ match ทะเบียนรถได้
พฤติกรรมที่ T8 เขียน test: `searchText` = ทะเบียนของแถว `FIRE`/`MISC` -> ไม่ match;
ทะเบียนเดียวกันบนแถว `CMI`/`VMI` -> match

### Hosts

- gate 2 จุด -> `product.PaymentStatus != PaymentStatus.UNPAID` (ข้อความ + status code เดิมเป๊ะ:
  cart 400 `"Unknown or inactive product."`, checkout 409 `"A cart product is no longer available."`)
- currency boundary จุดเดียว: `Money.Of(product.TotalPremium, "THB")` ใน `AddItemToCartCommand`
- `SfsQueryParser.ParsePaging(IQueryCollection query)` -> `(int Page, int Limit)` `public static`
  **`Parse` เรียก `ParsePaging` ต่อ** ⇒ logic clamp อยู่ที่เดียว; T7 แก้ `Math.Clamp(..., 1, 100)`
  บรรทัดเดียวใน `ParsePaging` ก็มีผลทั้ง 7 endpoint
- OpenAPI marker ใหม่ = **`ProductQueryParamsMarker`** (`SfsOpenApi.cs`, ข้าง `SfsQueryParamsMarker` เดิม)
  + `SfsOpenApi.AddProductQueryParameters(operation)` ประกาศ `page`/`limit`/`productFilters`
  transformer ที่ `Program.cs` เป็น `if` ก้อนที่สอง แยกจากก้อนเดิม ⇒ 6 endpoint ที่ใช้
  `SfsQueryParamsMarker` ไม่เปลี่ยนพฤติกรรมเลย
  refactor เล็ก: `AddPagingParameters` (private) คืน `IList<IOpenApiParameter>` ให้ทั้งสอง public method
  ใช้ร่วม — คืน list เพราะ compiler ไม่ propagate `??= []` ข้าม method (CS8602)
- `GET /products` `.WithDescription` เลิกโฆษณา SFS แล้ว
- **ไม่มี endpoint `GET /products/{id}`** ในโค้ด (`GetProductByIdQuery` ถูกเรียกจาก cart/checkout ภายใน
  เท่านั้น) ⇒ ไม่มี `.Produces<ProductView>` ให้แก้ (ข้อ 9 ของบรีฟเป็น no-op)

### `CreateProductRequest` — ลำดับ field สุดท้าย

ตรงกับ `ProductInput` ทุกตัว **ยกเว้นตัวแรก** (`MerchantId` มาจาก `actor` ไม่ใช่ body):

```csharp
internal sealed record CreateProductRequest(
    ProductGroup ProductGroup, DocumentType DocumentType, string DocumentNo, string SaleCode,
    decimal TotalPremium, string? PolicyYear = null, string? ReferenceBranch = null,
    string? ReferencePre = null, string? PolicySequenceNo = null, string? ReferenceYear = null,
    string? ReferenceNo = null, string? PolicyBranch = null, string? PolicyType = null,
    string? SaleFullName = null, string? BrokerCode = null, string? BrokerName = null,
    string? PolicyNumber = null, string? ApplicationNumber = null, string? PreviousPolicyNumber = null,
    string? EndorsementNumber = null, DateTime? StartDate = null, DateTime? EndDate = null,
    string? ShowName = null, string? LicensePlateNumber = null, decimal? NetPremium = null,
    decimal? Stamp = null, decimal? TaxVat = null, decimal? CommissionAmount = null,
    decimal? CommissionPercent = null);
```

(wire ของ premium เปลี่ยนจาก object `{"amount":..,"currency":".."}` เป็นตัวเลขเปล่า — breaking
สำหรับ client ที่เรียก `POST /products`; T8 ต้องแก้ payload ใน `ProductInsuranceFieldsRoundTripTests`)

### DI ที่เปลี่ยน

`ProductRepository` ctor = `(MerchantRuntimeDbContext db, IClock clock)` — **`ILogger` ถูกถอนออก**
(เหลือผู้ใช้เดียวคือ SFS silent-drop log ที่หายไปพร้อม `ProductSfs`) ไม่ต้องแก้ registration
(`MerchantRuntimePersistenceRegistration.cs:52` `AddScoped` เดิม + `IClock`/`SystemClock` singleton
ลงทะเบียนอยู่แล้ว) — T8 ที่สร้าง `ProductRepository` ตรง ๆ ในเทสต้องส่ง fake `IClock` แทน logger

### build ที่ยังแดง

`rtk proxy dotnet build pol-core.slnx` -> 64 error ใน **2 ไฟล์เท่านั้น** (ทั้งคู่เป็นของ T5):

| ไฟล์ | จำนวน | เจ้าของ |
|---|---|---|
| `src/Modules/Products/Products.Infrastructure/ProductConfiguration.cs` | 32 | T5 |
| `src/Persistence/Persistence.MerchantRuntime/Products/ProductConfiguration.cs` | 32 | T5 |

ไม่มี error ในไฟล์อื่นของ `src/` แล้ว; `tests/` ยังไม่ถูก compile (Persistence ล้มก่อน) — T8

### กับดักที่เจอ

1. **`dotnet build src/Hosts/Api` ยืนยัน Program.cs ไม่ได้ตรง ๆ** เพราะ Persistence ล้มก่อน ⇒ วิธีที่ใช้:
   patch ชั่วคราว (comment) ที่ `ProductConfiguration.cs` 2 ไฟล์ให้ compile ผ่าน -> build -> `git checkout --`
   คืนทั้งสองไฟล์ทันที (ยืนยันด้วย `git status` ว่า diff ของ T4 ไม่มีสองไฟล์นั้น). T5 ทำอยู่ตอนนี้ก็
   ระวังเรื่องนี้: ถ้า T5 เริ่มไปแล้วห้ามใช้วิธีนี้ (จะทับงานเขา)
2. **`operation.Parameters ??= []` ไม่ propagate ข้าม method** — helper ที่ set null-forgiving ให้แล้ว
   caller ยัง CS8602 ⇒ ให้ helper คืน list ที่ set แล้วออกมา (ที่ `-warnaserror` นี่คือ error ไม่ใช่ warning)
3. **`SfsLike` ไม่ได้อยู่ใน `ProductSfs.cs`** — อยู่ `src/BuildingBlocks/BuildingBlocks.Application/SfsLike.cs`
   ⇒ ลบ `ProductSfs.cs` แล้ว `SfsLike.Escape` ใน `ProductRepository` ยังใช้ได้ (ยังต้อง
   `using BuildingBlocks.Application;`)
4. **ลำดับ clause ของ window มาก่อน `PaymentStatusFilter`** โดยเจตนา (window เป็น floor) — ผลลัพธ์
   เหมือนกันเพราะเป็น AND ทั้งหมด แต่ SQL ที่ generate จะอ่านง่ายกว่าเวลา debug
5. `p.EndDate < today.AddMonths(2)` เป็น half-open ⇒ วันที่ 2026-09-30 (เมื่อ today = 2026-07-30)
   **ไม่เข้า** — ถ้าเจ้าของ SP บอกว่าต้อง inclusive ให้เปลี่ยนเป็น `<=` บรรทัดเดียว

commit ของ T4: `3bf4e11`

---

## T5 — EF + migration (เสร็จ)

แตะ 5 ไฟล์: `src/Modules/Products/Products.Infrastructure/ProductConfiguration.cs`,
`src/Persistence/Persistence.MerchantRuntime/Products/ProductConfiguration.cs`,
migration ใหม่ + designer + `PolDbContextModelSnapshot.cs`

### migration

`src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260730113459_ProductsSp52Alignment.cs`
(+ `.Designer.cs`) — timestamp `20260730113459` ใหม่กว่า `20260730081227_CheckoutChainDocumentFields`

`Up()` ตามลำดับจริงในไฟล์:

1. `DropIndex IX_Products_MerchantId_IsActive`
2. `DropColumn` x8: `BranchCode`, `IsActive`, `CreatedAt`, `TotalPremiumCurrency`,
   `NetPremiumCurrency`, `StampCurrency`, `TaxVatCurrency`, `CommissionCurrency`
3. `RenameColumn` x4: `TotalPremiumAmount`->`TotalPremium`, `NetPremiumAmount`->`NetPremium`,
   `StampAmount`->`Stamp`, `TaxVatAmount`->`TaxVat`
4. `AlterColumn` `decimal(19,4)` -> `decimal(19,2)` x5: 4 ตัวที่ rename (`TotalPremium` non-null,
   อีก 3 ตัว nullable) + `CommissionAmount` (**ไม่ rename** ชื่อคอลัมน์ตรง §5.2 อยู่แล้ว)

`CommissionPercent` `decimal(19,6)` ไม่ถูกแตะเลย; **ไม่มี `DropTable`** ⇒ ไม่ต้อง re-GRANT
(ยืนยันหลัง fresh replay: `pol_app` ยังมี `SELECT,INSERT,UPDATE,DELETE` บน `shop.Products`)

`Down()`: `RenameColumn` กลับ 4 -> `AlterColumn` กลับ `(19,4)` x5 -> `AddColumn` x8 (default
`''`/`0`/`0001-01-01` ตามที่ scaffolder ให้) -> `CreateIndex IX_Products_MerchantId_IsActive`

### EF config ทั้ง 2 ไฟล์ — mapping ค่าเงินสุดท้าย

```csharp
builder.Property(x => x.TotalPremium).HasPrecision(19, 2).IsRequired();
builder.Property(x => x.NetPremium).HasPrecision(19, 2);
builder.Property(x => x.Stamp).HasPrecision(19, 2);
builder.Property(x => x.TaxVat).HasPrecision(19, 2);
builder.Property(x => x.CommissionAmount).HasPrecision(19, 2);
builder.Property(x => x.CommissionPercent).HasPrecision(19, 6);
builder.Ignore(x => x.InsuranceType);
```

- **ไม่มี `HasColumnName` เหลือเลย** — ชื่อ CLR = ชื่อคอลัมน์ทั้ง 5 ตัวหลัง T2 (รวม `CommissionAmount`)
- `builder.Ignore` เหลือแค่ `InsuranceType` ตัวเดียว (computed Money 4 ตัวหายไปพร้อม T2)
- `ComplexProperty` หายทั้งสองไฟล์ ⇒ ไม่มี Money complex type บน `Product` แล้ว
  (`MoneyColumnMappingTests` ของ T8 ต้องเลิกนับ `Product` เป็น Money owner)
- `diff` บรรทัด `Property|HasIndex|Ignore` ระหว่างสองไฟล์: ต่างแค่ comment เรื่อง named `HasIndex`
  ⇒ mirror ตรงกันเป๊ะ

### ผลลัพธ์คอลัมน์จริง (fresh replay จาก DB เปล่า)

`docker compose down -v && docker compose up -d` (bootstrap `01-principals.sql` สร้าง `VCentralPay`
+ `pol_app` ให้) แล้ว `dotnet ef database update` -> apply migration ทั้ง 27 ตัวผ่าน

`INFORMATION_SCHEMA.COLUMNS` บน `shop.Products` = **33 คอลัมน์**:
`Id`, `MerchantId`, `ProductGroup varchar(10)`, `DocumentType varchar(20)`, `DocumentNo nvarchar(150)`,
`PolicyYear`, `ReferenceBranch`, `ReferencePre`, `PolicySequenceNo`, `ReferenceYear`, `ReferenceNo`,
`SaleCode varchar(20) NOT NULL`, `SaleFullName`, `BrokerCode`, `BrokerName`, `PolicyBranch`,
`PolicyType`, `PolicyNumber`, `ApplicationNumber`, `PreviousPolicyNumber`, `EndorsementNumber`,
`StartDate datetime2`, `EndDate datetime2`, `ShowName`, `LicensePlateNumber`,
`TotalPremium decimal(19,2) NOT NULL`, `NetPremium decimal(19,2) NULL`, `Stamp decimal(19,2) NULL`,
`TaxVat decimal(19,2) NULL`, `CommissionAmount decimal(19,2) NULL`,
`CommissionPercent decimal(19,6) NULL`, `PaymentStatus varchar(10) NOT NULL`, `PaidDate datetime2 NULL`

- คอลัมน์ที่ต้องหาย 8 + ชื่อเก่า 4 (`*Amount`): query `WHERE COLUMN_NAME IN (...)` คืน `none`
- index เหลือ 3: `PK_Products`, `IX_Products_MerchantId_PaymentStatus`,
  unique `IX_Products_MerchantId_DocumentNo` (ตัวที่ทำให้ `OrderBy(DocumentNo)` ของ T4 index-backed)
- EF seed 6 แถวใน `20260730072057` รันก่อน migration นี้จึงไม่พัง — ยืนยันด้วยการ replay จริง
  ไม่ใช่แค่ build

### round-trip Down/Up (REQ-10.3)

`dotnet ef database update 20260730081227_CheckoutChainDocumentFields` -> `Reverting...Done`,
คอลัมน์กลับเป็น **41 ตัว** คืนครบ 12 (8 ที่ drop + 4 ชื่อเก่า) + `IX_Products_MerchantId_IsActive`
กลับมา; แล้ว `dotnet ef database update` อีกครั้ง -> กลับสภาพ 33 คอลัมน์ `SUM(TotalPremium)` เท่าเดิม

**ข้อจำกัดที่ยอมรับ**: `Down()` คืนรูปร่างครบ แต่ **ค่า** ใน 8 คอลัมน์ที่ drop คืนไม่ได้
(`BranchCode`=`''`, `IsActive`=`0`, `CreatedAt`=`0001-01-01`, currency=`''`/NULL) — ธรรมชาติของ
rollback หลัง `DROP COLUMN` ไม่ใช่ข้อบกพร่องของ migration

### gate / build

- `bash scripts/check-migration-lineage.sh` -> `Migration lineage gate OK — all 4 existing migration
  IDs discoverable via PolDbContext.`
- `dotnet ef migrations has-pending-model-changes` -> `No changes have been made to the model since
  the last migration.`
- `rtk proxy dotnet build src/Hosts/Api` -> `Build succeeded.` 0 error 0 warning
- `rtk proxy dotnet build pol-core.slnx` -> **`src/` 0 error**; ที่เหลือ 192 error อยู่ใน `tests/`
  ทั้งหมด (ของ T8):

| ไฟล์ | error |
|---|---|
| `tests/Products.Tests/ProductTests.cs` | 68 |
| `tests/Architecture.Tests/ProductSfsTests.cs` | 42 |
| `tests/Architecture.Tests/ProductRepositoryListTests.cs` | 24 |
| `tests/Hosts.Tests/InsuranceCheckoutEndToEndTests.cs` | 16 |
| `tests/Hosts.Tests/WorkerWriteFloorTests.cs` | 12 |
| `tests/Hosts.Tests/ProductInsuranceFieldsRoundTripTests.cs` | 12 |
| `tests/Products.Tests/DocumentPaidOnOrderPaidConsumerTests.cs` | 8 |
| `tests/Architecture.Tests/WriteFloorTests.cs` | 4 |
| `tests/Architecture.Tests/ReadFloorTests.cs` | 4 |
| `tests/Products.Tests/ProductFilterDtoTests.cs` | 2 |

หมายเหตุให้ T8: `tests/Hosts.Tests/WorkerWriteFloorTests.cs` **ไม่อยู่ในรายการที่ T2 คาดไว้**
(เพิ่งโผล่ตอน tests เริ่ม compile ได้) และ `tests/Architecture.Tests/MoneyColumnMappingTests.cs`
กลับ **ไม่มี** error ตอน compile — แต่มันตรวจ Money mapping ตอน runtime จึงน่าจะแดงตอนรัน
เพราะ `Product` เลิกเป็น Money owner แล้ว

### กับดักที่เจอ

1. **scaffolder ไม่เดา rename ให้** — `dotnet ef migrations add` emit `DropColumn` + `AddColumn`
   สำหรับ 4 คอลัมน์ที่ต้อง rename (ข้อมูล premium จะหายทั้งตาราง) ⇒ ต้องแก้ `Up()`/`Down()`
   ของ migration **ใหม่** เป็น `RenameColumn` ด้วยมือ (แก้ไฟล์ใหม่ได้ ห้ามแตะไฟล์เก่า) แล้ว
   ปล่อย designer/snapshot เป็นของ `dotnet ef` ทั้งหมด — ตรวจว่าได้ผลด้วย
   `SUM(TotalPremium)` ก่อน/หลัง apply บน DB ที่มีข้อมูลจริง (37339.71 เท่ากัน) ไม่ใช่แค่ดู build
2. **ลำดับใน `Down()` ต้อง rename กลับ *ก่อน* `AlterColumn`** — `AlterColumn` อ้างชื่อคอลัมน์
   ที่มีอยู่ ณ ขณะนั้น ถ้าสลับลำดับจะพังตอน rollback (ต้องรัน `database update <ตัวก่อนหน้า>`
   จริงเพื่อยืนยัน ไม่มีทางรู้จาก build)
3. **`.env` อ่านด้วย Read/`cat` ไม่ได้ แต่ `set -a && . ./.env && set +a` ใน Bash call เดียวกับ
   `dotnet ef` ทำงานได้** — ต้องอยู่ call เดียวกันเพราะ shell state ไม่ข้าม call
4. `dotnet ef database update` ที่ไม่ระบุ target ใช้ connection จาก `POL_DESIGN_SQL` (`sa`) —
   `pol_app` ไม่มีสิทธิ์ DDL
5. **dev DB ตอนนี้เป็น DB สดที่ยังไม่ seed-demo** (T5 ทำ `down -v` เพื่อ replay ตามบรีฟ) ⇒
   T6 ต้องรัน `docker/bootstrap/seed-demo.sql` เองบน DB ตัวนี้; ใครที่พึ่ง demo data ก็เช่นกัน

commit ของ T5: `45d6e50`

---

## T6 — seed demo (เสร็จ)

แตะ 4 ไฟล์: `docker/bootstrap/seed-demo.sql`,
`.ai/specs/demo-seed-data/{requirements.md,design.md,tasks.md}` — ไม่แตะ `src/` และ `tests/` เลย

### `INSERT INTO shop.Products` — column list สุดท้าย (เหมือนกันทั้ง 2 ก้อน)

```
(Id, MerchantId, ProductGroup, DocumentType, DocumentNo, SaleCode, ShowName,
 TotalPremium, PaymentStatus, PaidDate)
```

หายไป: `BranchCode`, `IsActive`, `CreatedAt`, `TotalPremiumCurrency` (currency column เดียวที่ seed
เคยเขียน — อีก 4 ตัวไม่เคยอยู่ใน seed); rename `TotalPremiumAmount` -> `TotalPremium`;
**เพิ่มใหม่ `PaidDate`** (จำเป็นเพราะแถว PAID ต้องมีวันที่)

ค่าเงินเขียนเป็น 2 ตำแหน่งทุกตัว (`1200.00` แทน `1200.0000`) และ generator เปลี่ยน
`CAST(500 + Seq * 137.25 AS decimal(19,4))` -> `decimal(19,2)` (`Seq * 137.25` ให้ 2 ตำแหน่งอยู่แล้ว
จึงไม่มีค่าใดถูกปัด)

### แกนขายได้/ขายไม่ได้ ย้ายมาที่ `PaymentStatus` (REQ-2.4)

| เดิม | ใหม่ |
|---|---|
| hand-written `IsActive = 0` 3 แถว | `PaymentStatus = 'PAID'` + `PaidDate` = `DATEADD(day, -7/-5/-3, SYSUTCDATETIME())` |
| `CASE WHEN Seq % 7 = 0 THEN 0 ELSE 1 END` | `CASE WHEN Seq % 7 = 0 THEN 'PAID' ELSE 'UNPAID' END` + `PaidDate` = `DATEADD(day, -Seq, SYSUTCDATETIME())` |

`DocumentNo` ของ 13 แถวที่เป็น PAID:

- hand-written 3: `S001-69100/บต/900008` (vprivilege, `e9…0008`), `S001-69100/บต/900016`
  (vcommerce, `e9…0010`), `S001-69100/บต/900024` (vsouvenir, `e9…0018`)
- generated 10 (`Seq` = 7,14,21,28,35,42,49,56,63,70): `00098-69100/กธ/910007-10`,
  `…910014-10`, `…910021-10`, `…910028-10`, `…910035-10`, `…910042-10`, `…910049-10`,
  `…910056-10`, `…910063-10`, `…910070-10`

### ผล seed จริงบน dev DB `:11433`

`set -a && . ./.env && set +a && ./scripts/seed-demo.sh` -> `seed-demo: OK.` exit 0
(`shop.Products = 100`, ตารางอื่นเท่าเดิมทุกตัว: admin.Users 6, merch.Merchants 3,
txn.PspConnections 6, admin.MerchantAccess 4, admin.RoleAssignments 6, merch.Users 12,
merch.ExternalLogins 12, merch.RoleAssignments 6, shop.Carts 6, shop.CartItems 14,
shop.CheckoutSessions 4, shop.Orders 40, txn.PaymentSessions 36, shop.OrderItems 4,
shop.OrderItemPolicies 3) — รันซ้ำครั้งที่ 2 ผลเดิม exit 0 (idempotent ยังใช้ได้)

query ยืนยัน (stamp `sp_set_session_context` UserId/MerchantId ก่อนทุกครั้ง เพราะ `shop.Products` มี RLS
ไม่งั้นได้ 0 แถวเงียบ ๆ):

| query | ผล |
|---|---|
| `COUNT(*)` | 100 |
| `PaymentStatus = 'PAID'` | **13** (PaidDate NULL 0 แถว) |
| `PaymentStatus = 'UNPAID'` | **87** (PaidDate NULL 87 แถว = ครบ) |
| `COUNT(DISTINCT PaymentStatus)` | 2 |
| `WHERE TotalPremium <> ROUND(TotalPremium, 2)` | **0 แถว** |
| PAID ที่ถูกอ้างจาก `CartItems` ของ cart `Open` | **0 แถว** |

### สภาพ dev DB ตอนนี้

`:11433` (`pol-db`) = migrate ครบ 27 ตัว (T5) + **seed-demo แล้ว** (รัน 2 ครั้ง) ⇒ T7/T8/T9 ใช้ได้เลย
ไม่ต้อง seed ซ้ำ ถ้าไม่ `down -v` อีก

### spec `demo-seed-data`

- `requirements.md` REQ-5.4 เขียนใหม่: `TotalPremium DECIMAL(19,2)` (ทศนิยม <= 2, `Product.Create`
  throw ไม่ปัด) + มีทั้ง `'UNPAID'` (ขายได้) และ `'PAID'` + `PaidDate` (ขายไม่ได้) แทน `IsActive` 2 ค่า
- `design.md:174-175` (นอกบรีฟ แต่ระบุ `IsActive = 0` = 13 แถว จะค้างผิดถ้าไม่แก้) -> `PaymentStatus = 'PAID'`
  13 / `'UNPAID'` 87 + เพิ่มกฎว่าแถว PAID ห้ามถูกอ้างจาก cart ที่ยัง `Open`
- `tasks.md`: scope T3 ข้อ 4, verify query ข้อ 6 (`COUNT(DISTINCT IsActive)` -> `COUNT(DISTINCT PaymentStatus)`),
  ประโยค evidence 2 จุด
- `bash scripts/spec-trace.sh demo-seed-data` -> `OK: 'demo-seed-data' เกณฑ์ 33 ข้อ ถูกอ้างครบใน
  design.md และ tasks.md, EARS lint ผ่านทุกข้อ` (real pass ไม่ใช่ skip)
- `docker/bootstrap/assert-fresh-db.sql` **ไม่ต้องแก้** — grep `Products`/`IsActive` ไม่พบเลย (ยืนยันจากการอ่าน)

### กับดักที่เจอ / ที่คนถัดไปต้องรู้

1. **แค่ลบคอลัมน์ `IsActive` ไม่พอ** — ต้องเพิ่ม `PaidDate` เข้า column list ด้วย ไม่งั้นแถว PAID
   จะเป็น "จ่ายแล้วแต่ไม่รู้จ่ายเมื่อไหร่" (`Product.MarkPaid` set สองค่านี้คู่กันเสมอ)
2. **ต้องเช็คว่าแถวที่เปลี่ยนเป็น PAID ไม่ถูกอ้างจาก `shop.CartItems`** — 3 แถว hand-written ที่เคย
   `IsActive = 0` (`e9…0008/0010/0018`) โชคดีที่ไม่มีใน cart ใด ๆ (cart อ้าง `e9…0001-0005`,
   `0009-000e`, `0011-0014`) ถ้าเลือกแถวอื่นเป็น PAID demo `POST /checkouts` จะ 409 ตลอดตาม gate ใหม่ของ T4
   — query ตรวจอยู่ใน Evidence ของ task 6 ใช้ซ้ำได้
3. `shop.OrderItems` demo อ้าง product `e9…0006/000b/0009` ซึ่งยังเป็น `'UNPAID'` (เอกสารถูกสั่งซื้อแล้ว
   แต่ยังไม่ mark paid) — **จงใจไม่แตะ**: การ mark PAID ตอน order paid เป็นงานของ outbox consumer
   (`DocumentPaidOnOrderPaidConsumer`) ไม่ใช่ของ seed; ถ้า T8/T9 อยากได้ demo ที่สอดคล้องกว่านี้ต้องคุยแยก
4. **`sp_set_session_context` ก่อน query `shop.*`/`txn.*` เสมอ** เวลา verify นอกสคริปต์ — RLS ซ่อนแถว
   เงียบ ๆ ไม่ error (gotcha เดิมของ spec `demo-seed-data` T3 ยังจริงอยู่)
5. `.env` อ่านด้วย Read/`cat` ไม่ได้ แต่ `set -a && . ./.env && set +a && ./scripts/seed-demo.sh`
   ใน Bash call เดียวใช้ได้ (ยืนยันซ้ำจากกับดัก T5 ข้อ 3)
6. **`design.md:165-172` ของ spec `demo-seed-data` ยังบรรยาย generator เป็น plan-line x tier +
   `Name`/`PriceAmount`** ซึ่งค้างมาตั้งแต่ insurance-pivot (ก่อน spec นี้) — นอกขอบเขต T6, ถ้า T7
   จะเก็บ docs drift ให้ครบก็เป็นจุดที่ควรกวาด

commit ของ T6: `9ee82ab` (hash ถูกเติมใน commit เล็กถัดจากนั้น)

---

## T7 — cap 25 + docs (เสร็จ)

แตะ 12 ไฟล์: `src/Hosts/Api/{SfsQueryParser.cs,SfsOpenApi.cs}`,
`docs/reference/{search-filter-sort.md,entity-fields.md,platform-modules.md,src-structure.md}`,
`.ai/specs/search-filter-sort/{requirements.md,design.md,tasks.md}`,
`.ai/specs/checkout-chain-document-fields/{requirements.md,design.md,tasks.md}`,
`.ai/specs/insurance-pivot/design.md`, `.ai/specs/demo-seed-data/design.md`
(+ `tasks.md`/`HANDOFF.md` ของ spec นี้) — **ไม่แตะ `tests/`, `docker/`, `src/` นอก 2 ไฟล์**

### cap 25 — จุดที่แก้ครบทุกจุด

| จุด | เดิม | ใหม่ |
|---|---|---|
| `SfsQueryParser.cs` const ใหม่ | — | `private const int MaxLimit = 25;` (comment อ้าง §2 `@PageSize` + §5.1) |
| `SfsQueryParser.ParsePaging` | `Math.Clamp(TryInt(query["limit"], 25), 1, 100)` | `..., 1, MaxLimit` |
| `SfsOpenApi.AddPagingParameters` | `"clamp ในช่วง 1 ถึง 100"` | `"clamp ในช่วง 1 ถึง 25"` |

**บรีฟบอกว่า `SfsOpenApi` มี 2 ที่ — ของจริงมีที่เดียว**: T4 refactor เป็น helper `AddPagingParameters`
(private) ที่ทั้ง `AddQueryParameters` (marker เดิม) และ `AddProductQueryParameters` เรียกร่วมกันแล้ว
⇒ แก้บรรทัดเดียวมีผลทั้งสอง marker. เช่นเดียวกับ `ParsePaging` ที่ `Parse` เรียกต่อ
⇒ **cap 25 มีผลทั้ง 7 endpoint จากการแก้ 2 บรรทัด**

`rtk proxy dotnet build src/Hosts/Api` -> `Build succeeded. 0 Warning(s) 0 Error(s)`

### ผล grep residue

`grep -rn "1, 100\|1 ถึง 100\|\[1\.\.100\]" src/ docs/ .ai/` -> **ไม่เหลือ residue เชิงพฤติกรรมใน
`src/` และ `docs/` เลย**; ที่เหลือ 6 hit เป็นข้อความที่ตั้งใจคงไว้ทั้งหมด:

- ประโยคที่บรรยายการเปลี่ยนแปลงเอง — `products-sp-53-alignment/requirements.md:48` ("แทน clamp เดิมที่ `[1, 100]`"),
  `search-filter-sort/requirements.md:42` + `design.md:147` (เขียนค่าใหม่ 25 พร้อมระบุว่า supersede `[1..100]`)
- บันทึกประวัติ — `search-filter-sort/tasks.md:24`, `products-sp-53-alignment/tasks.md:28` (Evidence ของ T4),
  `HANDOFF.md:519` (section T4)

### docs/spec ที่แก้ + วิธีที่เลือก

**แก้ข้อความตรง ๆ** (เป็นข้อมูลผิดที่ทำให้คนเขียนโค้ด/SQL ผิดได้):

| ไฟล์ | ที่แก้ |
|---|---|
| `docs/reference/search-filter-sort.md` | เพดาน 6 จุด (`:121` ตาราง param, `:287` snippet parser, `:320` ตาราง error, `:338` §3, `:1022` testing guidance, `:1029-1033` test ตัวอย่าง — rename `Parse_clamps_limit_to_100` -> `_to_25`); ตัวอย่าง default-sort fallback เปลี่ยน `Product.CreatedAt` -> `Admins.Domain.Users.User` 3 จุด (`:585`, `:971`, `:1192` — verify แล้วว่า `User.CreatedAt` + `UserSfs` `"createdAt"` มีจริง); ลบ `ProductSfs` ออกจากรายการ as-built §13 + แก้ประโยค §9.2 ที่อ้าง `ProductSfs` comment; แก้รายการ endpoint ที่รองรับ SFS ที่หัวไฟล์ (ถอด `GET /api/v1/products`) |
| `docs/reference/entity-fields.md` | เขียนตาราง `shop.Products` **ใหม่ทั้งตาราง** เป็น 33 คอลัมน์จริง (ยึดผล `INFORMATION_SCHEMA.COLUMNS` ของ T5 + type/length จาก `ProductConfiguration`) + block 4 หัวข้อ (คืออะไร/บทบาท/พังยังไง/ทำงานยังไง) + หมายเหตุว่าคอลัมน์เดิมชุดไหนถูกลบ; `:11` ตัวอย่าง Money pair `PriceAmount` -> `UnitPriceAmount` |
| `docs/reference/platform-modules.md` | §5 บทบาท/ตารางฟีเจอร์/ย่อหน้าสถานะ เขียนใหม่เป็น "เอกสารประกัน" (เพิ่มแถว `MarkPaid`, แก้แถว List เป็น §2 contract, แก้แถว "แก้ไข/ปิดสินค้า" ว่า `Deactivate()` ถูกลบ); ข้อ 11 ใน gap list; §8 order item snapshot (`SumInsured…` -> field เอกสาร) |
| `docs/reference/src-structure.md` | §4.1 ตารางไฟล์ Products ใหม่ทั้งตาราง (เพิ่ม `DocumentPaidOnOrderPaidConsumer.cs`/enum ไฟล์, ถอด `GetProductsQuery.cs`/`ProductView.cs`); `Items/Item.cs` snapshot 2 จุด; ตาราง migration lineage เติม 4 ตัวล่าสุด (`OneOpenPaymentSessionPerOrder`, `ProductsInsuranceDocument`, `CheckoutChainDocumentFields`, `ProductsSp52Alignment`) |
| `.ai/specs/search-filter-sort/requirements.md:42` | REQ-2.2 เปลี่ยนค่าเป็น `[1..25]` + ระบุว่า supersede — คงรูป `- 2.2 ` + `THE SYSTEM SHALL` ⇒ gate ยังผ่าน |
| `.ai/specs/search-filter-sort/design.md:147` | D8 เปลี่ยนเป็น `[1..25]` |
| `.ai/specs/demo-seed-data/design.md:165-172` | generator เลิกเป็น "plan-line x tier + `Name`/`PriceAmount`" -> เขียนตามของจริง (`ROW_NUMBER` + `Seq % 4`/`Seq % 3` + `DocumentNo` + `TotalPremium = 500 + Seq * 137.25`) — จุดที่ T6 flag ไว้ |

**กำกับหมายเหตุแทนการเขียนย้อนอดีต** (spec ปิดแล้ว = บันทึกประวัติ, ทุกจุดใส่วันที่ 2026-07-30 +
ชื่อ spec ที่ supersede):

- `.ai/specs/checkout-chain-document-fields/requirements.md:50` (REQ-7.2 `IsActive=false`),
  `design.md:74`, `design.md:85` (assert ของ E2E), `tasks.md:34` (Done clause)
- `.ai/specs/insurance-pivot/design.md:325` (gate `IsActive == false` -> `PaymentStatus != UNPAID`)
- `.ai/specs/search-filter-sort/tasks.md:24` (เพดานเดิม) และ **`tasks.md:83` task 5** — เติมบล็อก
  หมายเหตุไว้ **หน้า** เนื้อ task เพราะบรรทัดนั้นนิยาม `ProductListItem(… bool IsActive, DateTime CreatedAt)`
  ตรง ๆ ซึ่งอ่านแล้วเข้าใจผิดว่าเป็นสเปกปัจจุบัน (แต่ไม่ลบข้อความเดิม)
- `docs/reference/search-filter-sort.md` — banner ใหม่ที่หัวไฟล์ (cap 25 + products ไม่มี SFS แล้ว +
  ประกาศว่าตัวอย่างที่ใช้ `Product`/`priceAmount`/`activeOnly` เป็นตัวอย่างเชิงสมมติ) และ note ที่หัว §12.2
  — **ตัดสินใจไม่ rewrite worked example ทั้งก้อน** เพราะมันยังสอน pattern merchant-scoped ได้ถูกต้อง
  และ rewrite ทั้งหัวข้อ = diff ใหญ่กว่าคุณค่าที่ได้ (ถ้าจะทำให้ครบจริงควรเป็น spec docs แยก)

### ผล spec-trace (เห็นคำว่า `OK:` จริงทั้ง 5 ตัว ไม่ใช่แค่ exit 0)

| spec | ผล |
|---|---|
| `products-sp-53-alignment` | `OK: … เกณฑ์ 53 ข้อ ถูกอ้างครบ…` |
| `search-filter-sort` | `OK: … เกณฑ์ 59 ข้อ ถูกอ้างครบ…` |
| `checkout-chain-document-fields` | `OK: … เกณฑ์ 21 ข้อ ถูกอ้างครบ…` |
| `insurance-pivot` | `OK: … เกณฑ์ 31 ข้อ ถูกอ้างครบ…` |
| `demo-seed-data` | `OK: … เกณฑ์ 33 ข้อ ถูกอ้างครบ…` |

### test ที่จะแดงเพราะ T7 (ให้ T8)

`tests/Hosts.Tests/SfsQueryParserTests.cs` — **ที่เดียว**:

- theory `Limit_is_clamped_into_1_to_100` (`:50`) -> rename เป็น `Limit_is_clamped_into_1_to_25`
- เคสที่ expected ต้องเปลี่ยนเป็น `25`: `[InlineData("1000", 100)]`, `[InlineData("100", 100)]`,
  `[InlineData("50", 50)]` (comment `// above ceiling -> 100` ก็ต้องแก้)
- เคสที่ **ไม่กระทบ**: `("1", 1)`, `("0", 1)`, `("-5", 1)`, `("abc", 25)`
- `A_huge_page_cannot_overflow_the_offset` (`:67`) ส่ง `limit=25` อยู่แล้ว -> ไม่กระทบ

`tests/Hosts.Tests/SfsOpenApiTests.cs` **ไม่แดง** — assert แค่ชื่อ parameter (`Assert.Contains("limit", names)`)
ไม่ assert description string (ตรวจแล้วทั้งไฟล์ `:52`, `:76`); ถ้า T8 อยากล็อกข้อความ 25 ก็เพิ่มได้แต่ไม่บังคับ

### กับดักที่เจอ

1. **บรีฟบอก 2 จุดใน `SfsOpenApi.cs` แต่ของจริง 1 จุด** — T4 dedupe ไปแล้ว. อ่านไฟล์ก่อนเชื่อเลขบรรทัด/จำนวนจุดในบรีฟ
   (เลขบรรทัดในบรีฟ `SfsQueryParser.cs:28`/`SfsOpenApi.cs:21` ก็เลื่อนไปแล้วทั้งคู่)
2. **task-gate ยิงเป็นครั้งที่ 4** — flip `[ ]` -> `[x]` ก่อนแล้วเติม Evidence ทีหลังถูก block จริง
   (ไฟล์ถูกเขียนไปแล้วตอน block ⇒ ต้องรีบเติม Evidence ใน Edit ถัดไป). ทำ flip + Evidence ใน Edit เดียวเถอะ
3. **`docs/reference/search-filter-sort.md` stale หนักกว่าที่บรีฟระบุ** — ไม่ใช่แค่เพดาน 100 กับ
   `Product.CreatedAt`: ทั้ง §7 (typed filter DTO) และ §12.2 (worked example) ยังเป็น `Product` ยุคก่อน
   insurance-pivot (`Name`/`Price`/`SumInsured`/`activeOnly`) และ §13 ยังลิสต์ `ProductSfs.cs` ว่ามีอยู่
   ⇒ เลือกวิธี "banner + note ต่อหัวข้อ" ไม่ rewrite ตัวอย่าง
4. **`entity-fields.md` ตาราง `shop.CheckoutSessionItems` (`:1050-1055`) และ `shop.OrderItems` (`:1111-1116`)
   ยังลิสต์คอลัมน์ `SumInsuredAmount`/`SumInsuredCurrency`/`InsurerName`/`CoverageDurationDays`** ซึ่ง
   `checkout-chain-document-fields` (PR #143) เปลี่ยนไปแล้ว — **ไม่ใช่ drift ของ spec นี้** และไม่ใช่ field
   ที่ REQ-11.3 ระบุ ⇒ ไม่แก้ (ต้องยืนยันคอลัมน์จริงจาก EF config ของ 2 ตารางนั้นก่อน) — **ยกให้ T9/lead ตัดสิน
   ว่าจะเก็บในสเปกนี้หรือแยก**
5. `.ai/specs/search-filter-sort/` มี gate ของตัวเอง — แก้ข้อความ REQ-2.2 ได้เพราะ spec-trace แมตช์ด้วยเลข
   `N.M` + คำ EARS ไม่ใช่เนื้อความ; แต่ต้องคงรูป `- 2.2 ` (มีจุดเกิน = silent-skip) และห้ามแตะเลขข้อ

commit ของ T7: `871c351`

---

## T8 — tests (เสร็จ)

แตะ 11 ไฟล์ใน `tests/` + ลบ 1 ไฟล์ — ไม่แตะ `src/`, `docker/`, `docs/`, spec อื่นเลย

### จำนวน test ต่อ project

"ก่อน" = ก่อน T8 ทั้ง 3 project **compile ไม่ได้** (192 error CS จาก T5) จึงรัน/นับไม่ได้เลย —
ตัวเลขในวงเล็บคือจำนวนเคสที่นับจากไฟล์ต้นทางเดิม (`[Fact]` + แต่ละ `[InlineData]`)

| project | ก่อน (นับจากไฟล์, รันไม่ได้) | หลัง (รันจริง) |
|---|---|---|
| `tests/Products.Tests` | ~40 | **53 passed / 0 failed / 0 skipped** |
| `tests/Hosts.Tests` | ~352 | **362 / 0 / 0** |
| `tests/Architecture.Tests` | ~232 (รวม `ProductSfsTests` 12 เคสที่ถูกลบ) | **225 / 0 / 0** (เต็มชุด ใช้เวลาจริง ~2 วินาที ไม่ถึง 9 นาทีตามที่ design.md เตือน) |

### ผลรัน `dotnet test` จริง (ทุก suite)

| suite | ผล |
|---|---|
| `Products.Tests` | 53 / 0 / 0 |
| `Hosts.Tests` | 362 / 0 / 0 |
| `Architecture.Tests` | 225 / 0 / 0 |
| `Integration.Tests` (`. ./.env.integration` ใน Bash call เดียวกัน, dev DB :11433) | **47 / 0 / 0** |
| Admins / BuildingBlocks / Carts / Checkouts / Divisions / Iam / Levels / Merchants / Offices / Orders / Payments / Positions / SharedKernel | 95 / 43 / 15 / 13 / 6 / 62 / 6 / 120 / 6 / 76 / 162 / 6 / 46 — ทุกตัว 0 failed, 0 skipped |

`rtk proxy dotnet build pol-core.slnx` -> `Build succeeded. 0 Error(s)` (0 warning);
`bash scripts/check-rename-identifiers.sh` -> `OK`

`tests/{Identity,Producer,Tenant}.Tests/` มีแต่ `bin/`+`obj/` ค้าง ไม่มี `.csproj` — ไม่ใช่ suite ที่หายไป

### ไฟล์ที่ลบ

`tests/Architecture.Tests/ProductSfsTests.cs` (`git rm`) — ไฟล์ที่มันทดสอบ (`ProductSfs.cs`) ถูก T4 ลบไปแล้ว

**กับดัก**: `scripts/check-rename-identifiers.sh` อ่าน **รายชื่อไฟล์จาก git index** แล้วเปิดไฟล์จริง
⇒ ถ้าลบไฟล์ด้วย `rm` แต่ยังไม่ `git rm` gate จะ crash ด้วย `FileNotFoundError` (ไม่ใช่ข้อความ gate ปกติ)
ต้อง stage การลบก่อนรัน gate

### เคสใหม่ที่เพิ่ม (ไล่ตาม REQ)

| REQ | เคส | ที่ไหน |
|---|---|---|
| 1.5 | `100.005`/`100.001` throw; `100.5`/`100.50`/`100` ผ่าน; breakdown ติดลบ + `TotalPremium` ติดลบ throw | `ProductTests` |
| 1.6 | คงครบ: `TotalPremium <= 0`, `StartDate > EndDate` (+ เท่ากันผ่าน), `Enum.IsDefined` x2, CMI+APPLICATION, MISC+APPLICATION ผ่าน, trim/normalise, `SaleCode` > 20 (ใหม่) | `ProductTests` |
| 2.1 / 2.3 | `MarkPaid` set PAID + `PaidDate` (เลิก assert `IsActive`); เอกสารที่ขายแล้วให้ค่าที่ gate อ่าน != UNPAID และหลุดจาก default list | `ProductTests`, `DocumentPaidOnOrderPaidConsumerTests`, `WorkerWriteFloorTests`, `InsuranceCheckoutEndToEndTests` |
| 3.1 / 3.2 | absent -> UNPAID, `ALL` -> ไม่กรอง, `PAID`/`UNPAID` ตรงตัว (ทั้งระดับ DTO และระดับ query จริงบน SQLite) | `ProductFilterDtoTests`, `ProductRepositoryListTests` |
| 3.3 | `saleCode` required + trim + MaxLength 20 (21 ตัวอักษร throw, 20 ผ่าน) + narrowing จริงใน `ListAsync` | `ProductFilterDtoTests`, `ProductRepositoryListTests` |
| 3.4 | `"branchCode": "001"` ถูก ignore เงียบ ๆ ไม่ใช่ 400 | `ProductFilterDtoTests` |
| 3.5 / 9.1 | `productFilters` absent / blank / whitespace / literal `null` -> throw (4 เคส) | `ProductFilterDtoTests` |
| 9.2 | `"unpaid"` (case-sensitive) / `"NOPE"` / `""` -> throw | `ProductFilterDtoTests` |
| 9.3 | 50003 / 50008 / 50009 + `From == To` ผ่าน | `ProductFilterDtoTests` |
| 4.1 | `limit` 1000/100/50/25 -> 25; 1/0/-5 -> 1; `abc` -> 25 | `SfsQueryParserTests` |
| 5.1 / 5.2 | ทะเบียนเดียวกัน: CMI/VMI match, FIRE/MISC ไม่ match (4 เคส) + non-Motor ยังค้น `DocumentNo` ได้ | `ProductRepositoryListTests` |
| 6.1 / 6.2 / 6.3 | **11 เคส A-K** ของตาราง window (fake `IClock` ตรึง today = 2026-07-30) + window ยัง AND แม้ client กรองอย่างอื่น | `ProductRepositoryListTests` |
| 7.2 | `OrderBy(DocumentNo)` + paging หน้า 1/2 + total หลัง filter | `ProductRepositoryListTests` |
| 7.4 | products declares เฉพาะ `page`/`limit`/`productFilters`; 4 endpoint ที่ถือ `SfsQueryParamsMarker` ยังครบ 5 ตัว | `SfsOpenApiTests` |
| 8.1 | projection 32 field ครบทุกช่อง + `InsuranceType` computed | `ProductRepositoryListTests` |
| 8.4 | mint `Money.Of(TotalPremium, "THB")` ที่ cart boundary | `InsuranceCheckoutEndToEndTests` |
| 1.2 / 10.1 | `Product` **ไม่เป็น** Money owner: `GetComplexProperties()` ว่าง + `TotalPremium` = คอลัมน์ `decimal(19,2)` | `MoneyColumnMappingTests` |

`MoneyColumnMappingTests` ไม่ได้ "ลบเคสให้ผ่าน" — เคสเดิม (`Product_TotalPremium_maps_to_decimal_19_4_and_char3`)
ถูก **แทน** ด้วยเคสกลับด้านที่จับ regression ถ้าใครเอา `ComplexProperty`/`decimal(19,4)` กลับมา

### สิ่งที่ยังไม่ได้ verify — เหลือให้ T9

1. **HTTP status code ของ gate ทั้งสอง** (cart add-item 400 `"Unknown or inactive product."`,
   checkout start 409 `"A cart product is no longer available."`) — gate อยู่ใน minimal-API lambda ของ
   `Program.cs` เรียกเป็น unit ไม่ได้ และ harness `WebApplicationFactory` ที่มีอยู่ boot host แบบ
   "no live DB" (ใช้ได้แค่ตรวจ metadata/OpenAPI) ⇒ ต้องยิงจริงบน dev :11433 ตาม REQ-12.6
   ที่ทำแทนไว้: assert **ค่าที่ gate อ่าน** (`GetProductByIdQuery` คืน `PaymentStatus != UNPAID`
   หลัง `OrderPaid`) + เอกสารหลุดจาก default list
2. `POST /api/v1/products` บน wire (premium เป็นตัวเลขเปล่า) — **ไม่มีเทสไหนใน repo ยิง endpoint นี้จริง**
   (grep `api/v1/products` ใน `tests/Hosts.Tests` เจอแต่ permission-site/route-scheme ที่ดู metadata)
   ⇒ breaking change ของ wire ยังไม่มี test ปิด, ต้องยิงมือตอน E2E
3. `docker compose down -v` + migrate + seed แล้วตรวจ `INFORMATION_SCHEMA.COLUMNS` (REQ-12.5) — T8 ใช้ DB
   ที่ T5/T6 เตรียมไว้ ไม่ได้ recreate ใหม่
4. `dotnet build -warnaserror` แบบระบุ flag ตรง ๆ — T8 รัน `dotnet build pol-core.slnx` ซึ่ง repo
   ตั้ง `-warnaserror` ใน props อยู่แล้ว (ผล 0 warning) แต่ REQ-12.1 เขียน flag ไว้ชัด ⇒ T9 รันตามตัวอักษร

### พบปัญหาใน src ที่ไม่ได้แก้

ไม่พบ bug ใน `src/` ที่ทำให้เทสแดง — ทุก error CS เป็นเทสที่ตามสเปกใหม่ไม่ทัน
ข้อสังเกต 2 ข้อที่ไม่ใช่ bug แต่ควรรู้ (commit ที่เกี่ยวข้อง: `871c351` = HEAD ก่อน T8):

1. **`ProductRepository.ListAsync` ทำให้ผลลัพธ์ขึ้นกับเวลาจริง** — เทสเดิมหลายตัวสร้างเอกสารด้วยวันที่
   hard-code (`2026-07-01`) แล้วใช้ `SystemClock`; พอมี search window เอกสารพวกนี้จะหลุด window เอง
   หลังผ่านไป 6 เดือน (เทสจะแดงเองในอนาคตโดยไม่มีใครแก้โค้ด) ⇒ T8 เปลี่ยน 3 ไฟล์ใน `Hosts.Tests`/
   `Architecture.Tests` ให้ inject fixed clock ทุกจุดที่สร้าง `ProductRepository`. **ใครเพิ่มเทสใหม่ที่
   แตะ `ProductRepository` ต้องส่ง fixed clock ห้ามใช้ `SystemClock`**
2. **HANDOFF T4 เขียนว่า "6 endpoint ที่ใช้ `SfsQueryParamsMarker`" — ของจริงในโค้ดมี 4**
   (`grep -n "SfsQueryParamsMarker" src/Hosts/Api/Program.cs` -> บรรทัด 983, 1585, 1693, 2079 =
   `/api/v1/reports/policies`, `/api/v1/admins/reports/policies`, `/api/v1/admins`, `/api/v1/admins/roles`)
   ⇒ `SfsOpenApiTests` theory ล็อกไว้ 4 ตัวตามของจริง ถ้าใครเชื่อเลข 6 จะไปหาที่ไม่มีอยู่

### กับดักที่เจอ

1. **task-gate ยิงเป็นครั้งที่ 5** — flip `[ ]` -> `[x]` ก่อนแล้วเติม Evidence ทีหลังถูก block จริง
   (ไฟล์ถูกเขียนไปแล้วตอน block) ⇒ ทำ flip + Evidence ใน Edit เดียวเถอะ (คำเตือน T1-T7 จริงทุกตัวอักษร)
2. **xUnit1025 เป็น error ไม่ใช่ warning** — `[InlineData(100.5)]` กับ `[InlineData(100.50)]` เป็น
   duplicate ในระดับ attribute (literal เป็น `double` เหมือนกัน) ⇒ เคส trailing-zero ต้องเขียนเป็น `[Fact]`
   ที่ assert ทั้ง `100.5m`/`100.50m`/`100m` ในตัวเดียว
3. **helper ที่มี default ค่า "ให้ผ่าน window" กลืนเคส NULL** — `Prod(startDate: null)` ที่ default เป็น
   `Today.AddDays(-1)` ทำให้เคส E/K (NULL semantics) กลายเป็นเคสปกติเงียบ ๆ ⇒ 11 เคส window สร้าง
   `Product.Create` ตรง ๆ ไม่ผ่าน helper
4. **`Assert.Equal` ของ `decimal` ไม่สน scale** (`100.50m == 100.5m`) ⇒ เคส trailing zero ยืนยันได้แค่ว่า
   guard ไม่ปฏิเสธ ไม่ได้ยืนยันว่าเก็บ scale ไว้ — scale จริงถูกล็อกที่ `MoneyColumnMappingTests` (19,2) แทน
5. `git rm` ก่อนรัน rename gate (ดูหัวข้อ "ไฟล์ที่ลบ")

commit ของ T8: `<เติมหลัง commit>`
