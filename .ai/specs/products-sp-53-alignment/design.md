# Design: products-sp-53-alignment

> Status: approved-for-implementation 2026-07-30
> อ่าน `requirements.md` + `HANDOFF.md` ก่อน. branch `feat/products-sp-53-alignment`
> เอกสารต้นเรื่อง: `docs/reference/vcentralpay-sp-quick-reference.pdf` (§2 หน้า 3, §3 หน้า 4, §4 หน้า 5, §5.1/§5.2 หน้า 6-7, §6 หน้า 8)

## หลักการ

`Product` = mirror ของ §5.2 result set (บวก `Id`/`MerchantId` เป็น technical key) และ
`GET /api/v1/products` = mirror ของ §2 input params (ลบ `@BranchCode`/`@CountMode`)
ทุกจุดที่ repo เคยเพิ่ม field ของตัวเองถูกถอนออก ไม่มีการ fabricate field ที่เอกสารไม่มี

การเปลี่ยนแปลงไหลจาก Domain ออกไปตามลำดับ dependency: Domain -> Application -> Hosts/Repository ->
EF/migration -> seed -> docs -> tests. record/ctor เป็น positional ทั้งสาย จึงให้ compiler พาไปทุก
call site เอง (**build จะแดงระหว่าง T2-T4 เป็นเรื่องปกติ**)

## Field mapping — `Product` ก่อน/หลัง

| เดิม | ใหม่ | คอลัมน์ |
|---|---|---|
| `string BranchCode` | ลบ | `BranchCode` DROP |
| `bool IsActive` | ลบ | `IsActive` DROP + `IX_Products_MerchantId_IsActive` DROP |
| `Guid MerchantId` | ลบ (แคตตาล็อกกลาง — ตัดสินเพิ่มระหว่าง PR) | `MerchantId` DROP + `IX_Products_MerchantId_PaymentStatus`/`IX_Products_MerchantId_DocumentNo` DROP -> `IX_Products_SaleCode_PaymentStatus` + unique `IX_Products_DocumentNo` (migration `20260730143112_ProductsCentralCatalogue`) |
| `DateTime CreatedAt` | ลบ | `CreatedAt` DROP |
| `Money TotalPremium` (ComplexProperty) | `decimal TotalPremium` | `TotalPremiumAmount` -> RENAME `TotalPremium` `decimal(19,2)`; `TotalPremiumCurrency` DROP |
| `decimal? NetPremiumAmount` + `string? NetPremiumCurrency` + computed `Money? NetPremium` | `decimal? NetPremium` | `NetPremiumAmount` -> RENAME `NetPremium` `decimal(19,2)`; `NetPremiumCurrency` DROP |
| `decimal? StampAmount` + `string? StampCurrency` + computed `Money? Stamp` | `decimal? Stamp` | `StampAmount` -> RENAME `Stamp` `decimal(19,2)`; `StampCurrency` DROP |
| `decimal? TaxVatAmount` + `string? TaxVatCurrency` + computed `Money? TaxVat` | `decimal? TaxVat` | `TaxVatAmount` -> RENAME `TaxVat` `decimal(19,2)`; `TaxVatCurrency` DROP |
| `decimal? CommissionAmountAmount` + `string? CommissionAmountCurrency` + computed `Money? CommissionAmount` | `decimal? CommissionAmount` | คอลัมน์ `CommissionAmount` ชื่อตรงอยู่แล้ว (แค่เปลี่ยน precision); `CommissionCurrency` DROP |
| `decimal? CommissionPercent` | คงเดิม | `CommissionPercent` `decimal(19,6)` คงเดิม |

DROP รวม 8 คอลัมน์: `BranchCode`, `IsActive`, `CreatedAt`, `TotalPremiumCurrency`,
`NetPremiumCurrency`, `StampCurrency`, `TaxVatCurrency`, `CommissionCurrency`
RENAME 4 คอลัมน์ตามตาราง; ทุกคอลัมน์เงินเป็น `decimal(19,2)`

field §5.2 ที่ **ไม่เปลี่ยน**: `SourceSystem`(= `ProductGroup`), `DocumentType`, `DocumentNo`,
`PolicyYear`, `ReferenceBranch`, `ReferencePre`, `PolicySequenceNo`, `ReferenceYear`, `ReferenceNo`,
`PolicyBranch`, `PolicyType`, `SaleCode`, `SaleFullName`, `BrokerCode`, `BrokerName`, `PolicyNumber`,
`ApplicationNumber`, `PreviousPolicyNumber`, `EndorsementNumber`, `StartDate`, `EndDate`, `ShowName`,
`CommissionPercent`, `PaidDate`, `LicensePlateNumber`, `PaymentStatus` + computed `InsuranceType`

### Rationale

- **ทำไม `decimal` ไม่ใช่ `Money`**: §5.2 ระบุ `decimal(19,2)` และไม่มีคอลัมน์ currency เลย —
  ระบบต้นทางเป็น THB เดียว การเก็บ currency ต่อแถวคือ field ที่ repo แต่งขึ้น
- **ทำไม Cart/Checkout/Order ยังเป็น `Money`**: `Money{Amount,Currency}` เป็น standing decision ของ
  โมดูลเงิน (DECIMAL(19,4)) และไม่ได้อ้าง §5.2 — งานนี้จึงไม่ถอน `Money` ทั้งระบบและไม่ต้องมี ADR
  ค่าเงินถูก mint เป็น THB ที่ **boundary จุดเดียว** คือ `src/Hosts/Api/Program.cs:683`
  `new AddItemToCartCommand(cartId, actor.MerchantId, body.ProductId, body.Quantity, Money.Of(product.TotalPremium, "THB"))`
  ต้นน้ำของบรรทัดนี้คือ `Product` (decimal) ปลายน้ำคือ Cart -> Checkout -> Order -> PaymentSession (Money)
- **ทำไม gate เป็น `PaymentStatus == UNPAID`**: `IsActive` ไม่มีใน §5.2 และ writer ทั้ง 3 จุดของ repo
  sync `IsActive` กับ `PaymentStatus` ล็อกกันอยู่แล้ว (`Deactivate()` ไม่มี prod caller) — แกน PAID
  จึงครอบ production ครบ 100% แหล่งเดียวที่ไม่ sync คือ seed demo (จัดการที่ REQ-2.4/10.5)
- **ทำไม order เป็น `DocumentNo`**: `CreatedAt` ถูกลบ ต้องมี order ที่ deterministic — unique index
  `IX_Products_DocumentNo` (unique ทั้งระบบ) หนุนอยู่แล้ว จึงไม่ต้องมี tie-breaker
- **ทำไม `paymentStatus` เป็น `string?` ไม่ใช่ `PaymentStatus?`**: ต้องแทน 3 สถานะ (`UNPAID`/`PAID`/`ALL`)
  ซึ่ง `PaymentStatus?` ทำไม่ได้ และ spec `checkout-chain-document-fields` ล็อกว่า enum ห้ามมีสมาชิก `ALL`
- **ทำไม Motor gate เป็น per-row**: ถ้า client ไม่ส่ง `productGroup` request เดียวมีทั้ง Motor/Non-Motor
  และ `InsuranceType` เป็น `builder.Ignore` (computed) แปลเป็น SQL ไม่ได้ จึงต้องเทียบ enum ตรง ๆ ใน predicate

## จุดแก้ตามชั้น (path จริง ยืนยันจากการสำรวจ 2026-07-30)

### 1. Domain — `src/Modules/Products/Products.Domain/`

- `Product.cs`: ลบ `BranchCode`(`:33`), `IsActive`(`:79`), `CreatedAt`(`:81`), `Deactivate()`(`:189`),
  computed `Money?` 4 ตัว(`:87-97`), `RequireThb`(`:209-213`) + call `:116-120`;
  `TotalPremium` -> `decimal`, breakdown 4 ตัว -> `decimal?`;
  `Create(ProductInput input)` ตัด param `createdAt`; `MarkPaid` เหลือ `PaymentStatus` + `PaidDate`;
  เพิ่ม guard scale <= 2 (REQ-1.5) แทน validation ที่หายไปจาก `Money.Of` (`Iso4217` + non-negative + scale)
- `ProductInput.cs`: ตัด `BranchCode`, premium -> `decimal`/`decimal?`
- comment fix (REQ-11.1): `DocumentType.cs:3`, `PaymentStatus.cs:3`, `ProductGroup.cs:3`, `Product.cs:52`

guard scale ที่ใช้: `decimal.Round(v, 2) != v` -> throw (ใช้กับ `TotalPremium` + breakdown ทุกตัวที่ไม่ null)

### 2. Application — `src/Modules/Products/Products.Application/`

- `ListProducts.cs`
  - `ProductListItem` = §5.2 ทั้ง 32 field + `Id` (เลิกเป็น slim subset; XML doc เดิมที่เขียนว่า
    "Deliberately a slim subset" ต้องแก้)
  - `ProductFilterDto`: เพิ่ม `[Required][MaxLength(20)] string? SaleCode`;
    `PaymentStatus` -> `string?` + computed `PaymentStatus? PaymentStatusFilter`
    (null เมื่อ `ALL`, ไม่งั้น `Enum.TryParse(ignoreCase: false)`, ค่าอื่น -> throw อ้าง 50007);
    `Parse(null/blank)` -> `throw new ArgumentException` (ไม่ใช่ `return null`);
    trim `SaleCode` ห้ามว่าง -> throw อ้าง 50005
  - `ListProductsQuery`: เลิก inherit `PagedQuery`, ประกาศ `Page = 1`/`Limit = 25` +
    `required ProductFilterDto ProductFilters`
  - XML doc `:19-24`: ตัดคำว่า "Optional" และคำอ้าง "authorization scope" ที่ผิด, เขียนกำกับว่า
    `@BranchCode` ไม่รองรับ และ `SaleCode` มาจาก client (ดู deviation)
- `GetProductById.cs`: คืน `ProductListItem`
- ลบไฟล์: `ProductView.cs`, `GetProductsQuery.cs`; ลบ `IProductRepository.ListByTenantAsync`

### 3. Hosts — `src/Hosts/Api/`

- gate x2: `Program.cs:679` (cart add-item, 400) และ `:776` (checkout start, 409)
  `!product.IsActive` -> `product.PaymentStatus != PaymentStatus.UNPAID` (ข้อความ error คงเดิม)
- currency boundary: `Program.cs:683` `Money.Of(product.TotalPremium, "THB")`
- `CreateProductRequest` (`:2204-2234`): ตัด `BranchCode`, premium -> `decimal`/`decimal?`
- `GET /products` (`:638-657`): เลิกเรียก `SfsQueryParser.Parse` — เพิ่ม
  `SfsQueryParser.ParsePaging(query)` คืน `(Page, Limit)` (reuse `TryInt` + `ClampPage` เดิม);
  `.WithDescription` ต้องเลิกโฆษณา SFS
- OpenAPI: เปลี่ยน metadata ของ products จาก `SfsQueryParamsMarker` เป็น marker ใหม่ที่ประกาศแค่
  `page`/`limit`/`productFilters` (`SfsOpenApi.cs` + transformer `Program.cs:293-300`)
- F2: `SfsQueryParser.cs:28` -> `Math.Clamp(TryInt(query["limit"], 25), 1, 25)` (มีผลกับ 7 endpoint);
  `SfsOpenApi.cs:21` ข้อความ "clamp ในช่วง 1 ถึง 100" -> 25

### 4. Repository — `src/Persistence/Persistence.MerchantRuntime/Products/`

`ProductRepository.ListAsync`
- ลบไฟล์ `ProductSfs.cs` ทั้งไฟล์ + เลิกเรียก `ApplySearch`/`ApplyFilters`/`ApplySort`
  (SFS machinery ยังมีผู้ใช้อื่น 6 ราย — admins/roles/policy report x2/master data — **ไม่ถูกแตะ**)
- `OrderBy(p => p.DocumentNo)` inline
- `Where(p => p.SaleCode == pf.SaleCode)` + ใช้ `pf.PaymentStatusFilter` (null = ALL)
- F3 (REQ-5): ครอบ predicate ทะเบียนรถด้วย per-row Motor gate
  `(p.ProductGroup == ProductGroup.CMI || p.ProductGroup == ProductGroup.VMI)`
- F5 (REQ-6): inject `IClock` (`BuildingBlocks.Application.IClock` + `SystemClock` singleton ลงทะเบียนแล้ว
  — pattern เดียวกับ `EfOutbox`) แล้ว AND window เข้าไปเสมอ:

  ```
  const int SearchWindowMonths = 6;    // §3/§4 + §5.1 SearchWindowMonths
  const int RenewalWindowMonths = 2;   // §3 RENEWAL window
  var today = _clock.UtcNow.Date;
  src = src.Where(p =>
      (p.DocumentType == DocumentType.RENEWAL
          && p.EndDate >= today && p.EndDate < today.AddMonths(RenewalWindowMonths))
      || (p.DocumentType != DocumentType.RENEWAL
          && p.StartDate >= today.AddMonths(-SearchWindowMonths)));
  ```

  Non-Motor §4 ใช้ `start_date` ย้อนหลัง 6 เดือน = rule ทั่วไป ไม่ต้องแยก branch
  ผลข้างเคียงที่ยอมรับ: แถวที่ `StartDate`/`EndDate` NULL หลุด window (SQL semantics ของ `>=` บน NULL)
  — §5.2 อนุญาต NULL แต่ไม่ระบุพฤติกรรม

### 5. EF + migration

- `ProductConfiguration.cs` **ทั้ง 2 ไฟล์** (`Products.Infrastructure/` = migration owner,
  `Persistence.MerchantRuntime/Products/` = runtime mirror — comment กำกับว่าต้องเหมือนกันเป๊ะ):
  ลบ mapping 8 คอลัมน์, เลิก `ComplexProperty` + `builder.Ignore` ของ computed Money 4 ตัว,
  `HasPrecision(19, 2)`, rename 4 คอลัมน์, ลบ `IX_Products_MerchantId_IsActive`
- `dotnet ef migrations add ProductsSp52Alignment --context PolDbContext --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api`
  (ต้องมี `POL_DESIGN_SQL` จาก `.env`) — timestamp ต้องใหม่กว่า `20260730081227`
- ตรวจ scaffold ให้เป็น `DropIndex` + `DropColumn` x8 + `RenameColumn` x4 + `AlterColumn` precision
  บน `shop.Products` เท่านั้น ไม่มี `DropTable` (จึงไม่ต้อง re-GRANT `pol_app`) และ `Down()` คืนได้ครบ
- regen `PolDbContextModelSnapshot.cs` (บล็อก Products ~`:1790-2005`) โดยให้ `dotnet ef` gen เอง

### 6. Seed demo — `docker/bootstrap/seed-demo.sql:186-250`

**แหล่งเดียวที่ข้อมูลไม่ sync**: มี ~13 แถวที่ `IsActive = 0` แต่ `PaymentStatus = 'UNPAID'`
(hand-written `:191-227` 3 แถว + generated `CASE WHEN Seq % 7 = 0` `:233-248`)

- ลบ 4 คอลัมน์ currency + `IsActive` + `CreatedAt` + `BranchCode` จาก INSERT ทั้ง 2 ก้อน,
  ลบ `CASE WHEN Seq % 7 = 0`
- แถวที่เคย `IsActive = 0` -> `PaymentStatus = 'PAID'` + `PaidDate` มีค่า
- `.ai/specs/demo-seed-data/requirements.md:102` (REQ-5.4) + `tasks.md:235,253,303-308,323`
  รวม verify query `SELECT COUNT(DISTINCT IsActive)` -> `PaymentStatus`
- `docker/bootstrap/assert-fresh-db.sql` ไม่ต้องแก้ (ไม่มี assertion เรื่อง `shop.Products`)

### 7. Docs + spec ที่อ้าง field ที่หายไป (spec-trace เป็น required check)

`.ai/specs/checkout-chain-document-fields/{requirements.md:50,design.md:74,85,tasks.md:34,36}` ·
`.ai/specs/insurance-pivot/design.md:325` · `.ai/specs/search-filter-sort/{requirements.md:42,tasks.md:83}` ·
`docs/reference/entity-fields.md:934-935` · `platform-modules.md:658,675` · `src-structure.md:243` ·
`search-filter-sort.md:121,287,320,338,585,971,1022,1029-1033,1192,1310`
(`search-filter-sort.md` ใช้ `Product.CreatedAt` เป็นตัวอย่างมาตรฐานของ default-sort fallback — เปลี่ยนตัวอย่าง)

### 8. Tests

| ไฟล์ | ทำ |
|---|---|
| `tests/Architecture.Tests/MoneyColumnMappingTests.cs:67-68` | ลบเคส `Product_TotalPremium_...` (gate บังคับ complex property + char(3)) — helper เป็น per-entity ลบเฉพาะเคสนี้ได้ |
| `tests/Architecture.Tests/ProductSfsTests.cs` | ลบทั้งไฟล์ (ตามไฟล์ที่ทดสอบ) |
| `tests/Architecture.Tests/ProductRepositoryListTests.cs` | เขียนใหม่: SaleCode narrowing, default UNPAID, ทะเบียนรถไม่ match แถว FIRE/MISC, window 6 เดือนตัดแถวเก่า, RENEWAL ใช้ `EndDate` window 2 เดือน, order by `DocumentNo` — inject fake `IClock` ให้ผลคงที่ |
| `tests/Products.Tests/ProductTests.cs` | ตัด 3 เคส BranchCode (`:61,65,81`), เคส IsActive/CreatedAt (`:41-42,161,169-171`), premium เป็น decimal, เพิ่มเคส scale > 2 -> throw |
| `tests/Products.Tests/ProductFilterDtoTests.cs` | absent -> 400; SaleCode required + trim/blank (50005); `ALL` vs default UNPAID; paymentStatus เถื่อน -> 400 (50007) |
| `tests/Products.Tests/DocumentPaidOnOrderPaidConsumerTests.cs:21,36,53` | `MarkPaid` ไม่เซ็ต IsActive แล้ว |
| `tests/Hosts.Tests/InsuranceCheckoutEndToEndTests.cs:94,107,216` · `WorkerWriteFloorTests.cs:123,136` | gate ใหม่ + `ProductInput` signature ใหม่ |
| `tests/Hosts.Tests/ProductInsuranceFieldsRoundTripTests.cs:76,92` | premium decimal |
| `tests/Hosts.Tests/SfsQueryParserTests.cs:50` | `Limit_is_clamped_into_1_to_100` -> 1..25 |
| `tests/Hosts.Tests/SfsOpenApiTests.cs` | products ไม่โฆษณา `filters`/`sort`/`search` |
| `tests/Hosts.Tests/*` ที่ยิง `GET /api/v1/products` | ต้องส่ง `productFilters` (มี `saleCode`) ไม่งั้น 400 |

## Traps (อ่านก่อนเริ่มทุก task)

1. **task gate**: flip `- [ ]` -> `- [x]` และเขียน `Evidence:` ใน **Edit เดียวกัน**; บรรทัด `Evidence:` ห้ามมี `-` นำหน้า
2. **spec-trace silent-skip**: heading ที่ไม่ใช่ `## REQ-N:` ทำให้ `scripts/spec-trace.sh` ข้ามการตรวจแล้ว exit 0 — ต้องเห็นบรรทัดขึ้นต้น `OK:` จริง
3. **ชื่อ CLR ไม่ตรงชื่อคอลัมน์**: `CommissionAmountAmount` -> คอลัมน์ `CommissionAmount` (ตรงอยู่แล้ว) และ `CommissionAmountCurrency` -> คอลัมน์ `CommissionCurrency` — ตอน grep residue ต้อง grep ทั้งชื่อ CLR และชื่อคอลัมน์
4. **dual-config**: `ProductConfiguration` มี 2 ไฟล์ ต้องแก้คู่ — `EntitySchemaMappingTests`/`ModelConsistencyTests` จับได้ถ้าหลุด
5. **ห้ามแก้ migration/designer เก่า** — `scripts/check-migration-lineage.sh` เฝ้าอยู่; timestamp ต้องใหม่กว่า `20260730081227` (ให้ `dotnet ef` gen เอง)
6. **rename gate**: `scripts/check-rename-identifiers.sh` สแกน token ที่ retired ในไฟล์ที่ git track — ระวังตั้งชื่อ helper ใน test ใหม่ให้ชนคำที่เลิกใช้ (เคสจริง: helper `Line(` ชน retired token จาก OrderLine -> OrderItem)
7. **hook block compound command**: `git add && git commit` โดน block = ทั้งก้อนตาย ต้องเช็คว่า part ไหนรันไปแล้ว และตรวจ committed tree ด้วย `git show --stat HEAD` ไม่ใช่ working tree
8. `.env*`: Read/Edit และ Bash file utils โดน deny — ใช้ `source .env.integration` ใน Bash call เดียวกับ `dotnet test` เท่านั้น
9. `Program.cs` มี using alias `DocumentType = Products.Domain.DocumentType` (กัน Scalar.AspNetCore ชน) — อย่าลบ
10. **`unset GH_TOKEN`** ก่อนทุกคำสั่ง git/gh (GH_TOKEN ค้างใน profile ทำ push ล้ม)
11. `Architecture.Tests` เต็มชุดใช้เวลา ~9 นาที — เผื่อเวลา ไม่ใช่ hang
12. **build แดงระหว่าง T2-T4 เป็นเรื่องปกติ** ห้ามไปแก้ไฟล์นอก task ตัวเองเพื่อดับแดง ให้บันทึกใน HANDOFF ว่าอะไรยังแดง

## Verification

1. `dotnet build pol-core.slnx -warnaserror` — 0 error / 0 warning
2. `dotnet test` ทุก suite + `source .env.integration && dotnet test tests/Integration.Tests`
3. `bash scripts/spec-trace.sh products-sp-53-alignment` -> ต้องเห็น `OK:`
4. `bash scripts/check-rename-identifiers.sh` + `bash scripts/check-migration-lineage.sh`
5. `docker compose down -v && up` -> migrate + seed สะอาด -> `SELECT * FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Products'` เหลือคอลัมน์ตรง §5.2
6. E2E บน dev DB `:11433`:
   - ไม่ส่ง `productFilters` -> 400; `saleCode` เว้นวรรค -> 400 (50005)
   - `{saleCode:"00098"}` -> เฉพาะ UNPAID + `StartDate` ย้อนหลังไม่เกิน 6 เดือน, เรียงตาม `DocumentNo`
   - `{..., paymentStatus:"ALL"}` -> UNPAID+PAID; `"XXX"` -> 400 (50007)
   - `{..., documentType:"RENEWAL"}` -> คัดด้วย `EndDate` window 2 เดือน ไม่ใช่ 6 เดือน
   - `searchText` = ทะเบียนรถแถว MISC -> 0 ผล; แถว VMI -> เจอ (§3.2)
   - `limit=1000` -> response `limit` = 25; `filters=`/`sort=` ไม่มีผลและไม่โผล่ใน `/scalar`
   - checkout chain: เอกสาร PAID -> add to cart 400 / checkout 409; UNPAID -> ผ่านจนจ่ายเงิน แล้ว `OrderPaid` -> เอกสารกลายเป็น PAID -> ซื้อซ้ำไม่ได้

## Requirement Traceability

| เกณฑ์ | จุดใน design |
|---|---|
| REQ-1 (1.1-1.7) | ตาราง "Field mapping" + Rationale (decimal ไม่ใช่ Money) + จุดแก้ชั้น 1 (Domain) |
| REQ-2 (2.1-2.4) | Rationale (ทำไม gate เป็น PaymentStatus) + จุดแก้ชั้น 1 (ลบ `Deactivate`/`MarkPaid`), ชั้น 3 (gate x2), ชั้น 6 (seed) |
| REQ-3 (3.1-3.6) | จุดแก้ชั้น 2 (`ProductFilterDto`: SaleCode required, `paymentStatus` string, `Parse` throw) + Rationale (ทำไม string ไม่ใช่ enum) + นอกขอบเขต (`@BranchCode`) |
| REQ-4 (4.1, 4.2) | จุดแก้ชั้น 3 หัวข้อ F2 (`SfsQueryParser`, `SfsOpenApi`) + ชั้น 7 (docs SFS) |
| REQ-5 (5.1, 5.2) | จุดแก้ชั้น 4 หัวข้อ F3 + Rationale (ทำไม Motor gate เป็น per-row) |
| REQ-6 (6.1-6.4) | จุดแก้ชั้น 4 หัวข้อ F5 (code block window + `IClock` + ค่าคงที่ 2 ตัว) |
| REQ-7 (7.1-7.5) | จุดแก้ชั้น 2 (`ListProductsQuery` เลิก inherit `PagedQuery`), ชั้น 3 (`ParsePaging` + OpenAPI marker ใหม่), ชั้น 4 (ลบ `ProductSfs.cs` + `OrderBy(DocumentNo)`) |
| REQ-8 (8.1-8.4) | จุดแก้ชั้น 2 (`ProductListItem` 32 field, ลบ `ProductView`/`GetProductsQuery`/`ListByTenantAsync`) + Rationale (currency boundary `Program.cs:683`) |
| REQ-9 (9.1-9.4) | จุดแก้ชั้น 2 (throw อ้าง 50005/50007 + คงเช็ค 50003/50008/50009) + Traps |
| REQ-10 (10.1-10.6) | จุดแก้ชั้น 5 (EF config x2 + migration + snapshot), ชั้น 6 (seed + spec demo-seed-data) |
| REQ-11 (11.1-11.3) | จุดแก้ชั้น 1 (comment fix 4 ไฟล์), ชั้น 7 (docs/spec ที่อ้าง field ที่หายไป) |
| REQ-12 (12.1-12.6) | หัวข้อ Verification (ข้อ 1-6) + Traps |

## Follow-up ที่บันทึกไว้ (ไม่ทำในงานนี้)

- F6 `@CountMode` + §5.1 envelope -> spec แยก
- map `@BranchCode` -> `ReferenceBranch` เมื่อเจ้าของ SP ยืนยัน
- actor -> sale claim (ย้าย `SaleCode` ไป server-side authorization context)
- SP adapter จริง (motordb / centerdb)
