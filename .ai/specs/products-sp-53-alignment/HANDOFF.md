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
| T2 | Domain: `Product.cs`, `ProductInput.cs` | รอ |
| T3 | Application: read model, `ProductFilterDto`, query, ลบ dead code | รอ |
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
