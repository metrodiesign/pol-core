# HANDOFF — docs sync (products-alignment)

Rolling handoff. อ่านทั้งไฟล์ก่อนเริ่ม แล้วต่อท้าย section ของตัวเอง ก่อนจบงาน — ห้ามลบ/แก้ section ของคนก่อน

---

## T1 — canon layer (`.ai/shared/*`, `README.md`)

### ย่อหน้า canonical "Product คืออะไร" (copy ไปใช้ตรงกันทุกไฟล์)

`Product` = เอกสารประกันที่ขายได้ 1 รายการ (ใบสมัคร/กรมธรรม์/ต่ออายุ/สลักหลัง — `DocumentType`
APPLICATION/POLICY/RENEWAL/ENDORSEMENT) ที่อยู่ใน**แคตตาล็อกกลาง** (`shop.Products`, ไม่มีคอลัมน์
`MerchantId`, ไม่มี query filter) mirror ผลลัพธ์ `docs/reference/vcentralpay-sp-quick-reference.pdf`
§5.2 ทั้ง 32 field ตรง ๆ — **ไม่ใช่ "แผนประกัน/quote เบี้ย"** และไม่มี `SumInsured`/`CoverageDurationDays`/
`Insurer` อีกต่อไป (ฟิลด์เหล่านี้ถูกลบตั้งแต่ spec `products-sp-53-alignment`, 2026-07-30). ราคาขาย =
`TotalPremium` เป็น `decimal(19,2)` ล้วน ไม่ใช้ `Money` value object เพราะ source system เป็น THB
อย่างเดียว (ไม่มี currency column). Catalogue เป็น **read-only over HTTP** — endpoint เดียวคือ
`GET /api/v1/products` (บังคับ `productFilters` JSON param ที่ต้องมี `SaleCode` เป็นตัวจำกัดขอบเขต),
เอกสารเข้าระบบผ่าน importer/seed ไม่ใช่ HTTP write (`POST /products` ถูกถอดไปแล้ว —
`CreateProductCommand` ยังอยู่เป็น write seam ให้ importer/test เท่านั้น ไม่ reachable ผ่าน HTTP).
`InsuranceType` (Motor/NonMotor) เป็น derived property จาก `ProductGroup`, ไม่เก็บลง DB
(`builder.Ignore(x => x.InsuranceType)`).

### ไฟล์ที่แก้ + แก้อะไร

- `.ai/shared/PROJECT_CONTEXT.md` — ย่อหน้า Purpose (ลบ `SumInsured`/`CoverageDurationDays`/`Insurer`,
  เขียนนิยาม Product ใหม่ตามย่อหน้า canonical ข้างบน) + bullet แรกใน Key Features (แผนประกันเป็น field ->
  Product = เอกสารประกันในแคตตาล็อกกลาง, read-only) + bullet ผู้เอาประกันต่อ OrderItem (snapshot
  "เงื่อนไขประกัน SumInsured/..." -> "ราคา/เงื่อนไขจาก Product")
- `.ai/shared/stack/dotnet.md:91` — ตัวอย่าง computed/unmapped member ที่แปล LINQ->SQL ไม่ได้ เปลี่ยนจาก
  `Product.Price => Money.Of(...)` (ไม่มีจริงแล้ว) เป็น `Product.InsuranceType` (ยืนยันจริงที่
  `Products.Infrastructure/ProductConfiguration.cs:57`, `builder.Ignore(x => x.InsuranceType)`) และ
  `AdminRole.PermissionKeys` -> `Role.PermissionKeys` (`Iam.Domain.Roles.Role.cs:49`, `AdminRole` ไม่มีอยู่
  แล้วหลัง rf2/hierarchical-naming)
- `.ai/shared/stack/dotnet.md:95` — ตัวอย่าง area-root empty-pattern trailing-slash bug ที่ใช้
  `MapGroup("/products")`/`api.MapPost("/products")` เปลี่ยนไปใช้ `/carts` แทน (ยังมี `POST /carts`
  จริงใน `Program.cs` ส่วน `/products` เหลือแค่ `GET` list เดียว ไม่มี area-root write ให้เจอบั๊กแบบนี้
  อีกแล้ว) — เพิ่มโน้ตสั้นท้ายบรรทัดอธิบายว่าทำไม `/products` ไม่ใช่ตัวอย่างที่ใช้ได้แล้ว (บรรทัด 96
  ที่พูดถึง `GET /api/v1/products` no-slash ยังถูกอยู่ — endpoint นี้ยังมีจริง จึงไม่แตะ)
- `README.md:12-13` — รายการโมดูล "Tenant provisioning, Admin (Google SSO + RBAC), Producer (Google SSO +
  registration)" (module `Producer`/`Tenant` ไม่มีอยู่แล้ว — รวมเป็น `Merchants` ตั้งแต่ rf1) เปลี่ยนเป็น
  "Merchants (provisioning + merchant-user Google SSO + registration, รวม Tenant+Producer เดิม, rf1),
  Admin (Google SSO + RBAC)"
- `README.md` Topology table (~83-90) — table ทั้งก้อนพัง (drift ที่พบเพิ่มนอกเหนือ scope เดิมที่ brief
  ระบุแค่บรรทัด 90 แต่แก้รวดเพราะอยู่ตารางเดียวกันและผิดจริงทุกแถว): `pol_admin` keyed control-plane
  ไม่มีอยู่แล้ว (เหลือ `pol_app` ตัวเดียว), "Worker merge เข้ามาแล้ว" ล้าสมัยกว่านั้นอีกขั้น — Worker host
  ถูก**ถอดทั้งก้อน**ไปแล้ว (commit `cf48bf9`, ไม่ใช่แค่ merge), มี FE console **2 ตัว** ไม่ใช่ 1
  (admin `:5200` + merchant-user `:5300`, path เดิม `/admin/*`+`/merchants/*` ผิด ต้องเป็น
  `/api/v1/admins/*` / `/api/v1/merchants/*`) — sync ให้ตรงกับ `docs/runbooks/local-dev-run.md` §3/§4.3
  ที่แก้ถูกไปแล้วก่อนหน้านี้ (README ไม่ได้ sync ตาม)

### drift ที่เจอแต่ไม่ได้แก้ (นอกขอบเขต T1 — ทิ้งให้ T2-T9 หรือคนถัดไป)

- `.ai/shared/ARCHITECTURE.md` และ `.ai/shared/CODING_STANDARDS.md` — grep หา "Product" แล้วไม่เจอข้อความ
  ที่ผิดจริง (ไม่มีที่ไหนอ้างว่า `POST /products` ยังอยู่ หรือ wire `product.create` เข้า endpoint จริง) —
  **ไม่แตะทั้ง 2 ไฟล์** ตามกฎ surgical-only-if-wrong; permission key `Iam.Domain.Permissions.Keys.ProductCreate`
  (`"product.create"`) ยังอยู่ในโค้ดจริง (`Keys.cs:54`) เป็น orphan key ไม่มี endpoint ใช้ — ไม่มีเอกสารไหน
  พูดถึงมันเลยจึงไม่มีอะไรต้องแก้ ณ ตอนนี้ แต่ถ้ามีคนเพิ่มมันเข้า docs ทีหลังต้อง note ว่าเป็น orphan
- `.ai/shared/ARCHITECTURE.md` ส่วน API path scheme (~66-72) ยังพูดถึง 13 area รวม `products` เฉย ๆ ไม่ระบุ
  ว่า area ไหน read-only/write — ถูกต้องอยู่ (ไม่ได้ระบุ verb ต่อ area ตั้งแต่แรก) ไม่ต้องแก้
- `docs/reference/platform-modules.md`, `docs/reference/payment-orchestration-modules.md`,
  `docs/reference/search-filter-sort.md` — **ไม่ได้ตรวจ** (นอกขอบเขตชัดเจน "ห้ามแตะ docs/reference/*" —
  เป็นของ task อื่น) แต่คาดว่ามี drift เดียวกัน (Product = แผนประกัน / SFS ยังพูดถึง filters/sort/search
  บน Products) เพราะ `.ai/shared/CODING_STANDARDS.md:56` เองยังบอกว่า SFS เป็น convention บังคับของ
  list endpoint "จนกว่ามี ADR" — ไม่ชัดว่า Products ที่ถอด SFS ไปแล้วนับเป็นข้อยกเว้นที่บันทึกไว้หรือยัง
- `.env.example` / `docker/bootstrap/seed-demo.sql` — ไม่ได้ตรวจว่า seed demo data สอดคล้องกับ schema
  ใหม่ของ `Product` (drop 8 คอลัมน์ + rename 4 คอลัมน์) หรือไม่ — memory เดิม (`products-sp-53-alignment
  PR #144`) บันทึกไว้ว่า "seed ไม่มี StartDate/EndDate ⇒ GET /products บน demo คืน 0 แถว" ยังไม่ยืนยันว่าถูก
  fix แล้วหรือยัง
- ARCHITECTURE.md/CODING_STANDARDS.md ไม่มี "canonical entity list" รวม `Product` แยกต่างหาก (ต่างจาก
  entity อื่นอย่าง `Merchant`/`Payments.Domain.Session` ที่มีบรรทัด rename ชัดเจนใน CODING_STANDARDS.md) —
  ถ้า T2-T9 อยากเพิ่ม `Product`/`ProductGroup`/`DocumentType` เข้า naming table ตรงนั้นก็ทำได้ แต่ไม่ใช่
  bug ที่ต้อง fix (แค่ไม่มีข้อมูลอยู่แต่แรก ไม่ใช่ข้อมูลผิด)

commit ของ T1: `2de14a5` (5 ไฟล์ตามขอบเขต — จริง ๆ แก้ 3 ไฟล์ เพราะ ARCHITECTURE.md/CODING_STANDARDS.md
ตรวจแล้วไม่พบ drift ให้แก้)
