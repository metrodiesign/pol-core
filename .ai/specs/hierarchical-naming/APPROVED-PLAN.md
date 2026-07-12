# Spec: hierarchical-naming — nest namespace/type + route ทั้งโปรเจกต์

## Context

repo วันนี้ใช้ **flat compound naming** ทั้ง CLR type และบาง route: type อย่าง `MerchantUserRoleDefinition`,
`PlatformUserSessionDecision`, `PaymentSession`, `CartItem` เอา parent มาแปะเป็น prefix แทนที่จะซ้อนเป็น
namespace; และ route เหลือ compound area `/api/v1/merchant-users/*` กับ `/api/v1/admins/master-data/*`

ต้องการเปลี่ยนเป็นทรง **hierarchical**: namespace ซ้อนตาม sub-domain (`Merchants.Domain.Users.User`,
`Admin.Domain.Roles.Role`, `Cart.Domain.Items.Item`) โดยตัด parent prefix ออกจากชื่อ type และย้าย route
compound ที่เหลือให้ nest ตาม resource จริง

**ขนาดจริง: 262 จาก 404 ไฟล์ `.cs` ใน `src/`+`tests/`** — repo-wide refactor ไม่ใช่ module rename

**Workflow: Design-First** — behavior ต้องไม่เปลี่ยนเลย (pure rename, behavior-preserving);
สิ่งที่ต้องตัดสินคือ *โครง* (namespace layout ต่อโมดูล, ambiguity policy, filter/CORS re-binding, cutover)
ซึ่งเป็น design ล้วน ๆ — requirements (EARS "preserve X" + guard ใหม่) เขียนได้แม่นกว่าหลัง design ล็อก

## Locked decisions (ผู้ใช้ตัดสิน 2026-07-12 — ห้าม re-litigate)

| # | เรื่อง | ผลตัดสิน |
|---|--------|----------|
| D1 | Scope | **CLR naming + route** ทั้งคู่ |
| D2 | Module scope | **ทุกโมดูล 7 ตัว** — Admin, Merchants, Products, Cart, Checkout, Orders, Payments |
| D3 | Nesting shape | **sub-folder + sub-namespace ในทุก layer project ที่มีอยู่** (`<Module>.Domain.<Sub>.*`, `.Application.<Sub>.*`, `.Infrastructure.Persistence.<Sub>.*`) — **ไม่เพิ่ม csproj ใหม่** |
| D4 | Type names | **ตัด parent prefix หมด** — `Users.User`, `Users.Session`, `Roles.RoleDefinition`, `Items.Item`, `Sessions.Session` |
| D5 | Admin module | **รวม 2 ตระกูลเป็นแกนเดียว** — เลิก `Platform*` ทิ้ง: `PlatformUser` -> `Admin.Domain.Users.User`, `PlatformUserSession` -> `Users.Session`, `AdminRole` -> `Admin.Domain.Roles.Role` — ให้ตรงกับ route `/admins` + schema `admin` ที่ขัดกันอยู่วันนี้ |
| D6 | Wire/persisted strings | **ขยับทุกอย่าง** — DB table, permission key, auth-scheme name, outbox contract name |
| D7 | Hosts/Api | **nest ด้วย** — `Api/<Area>/` + namespace `Api.<Area>` |
| D8 | route: merchant-users | `/api/v1/merchant-users/*` -> **`/api/v1/merchants/users/*`** (area `merchants` ใหม่) |
| D9 | route: provision merchant | `POST /api/v1/admins/merchants` -> **`POST /api/v1/merchants`**; `GET /admins/merchants/{code}` -> `GET /api/v1/merchants/{code}` |
| D10 | route: admin approve/reject | `/api/v1/admins/merchant-users/{subject}/approve` -> **`/api/v1/admins/merchants/users/{subject}/approve`** |
| D11 | route: master-data | `/api/v1/admins/master-data/*` -> nest เป็น sub-resource (design เลือกรูปสุดท้าย) |
| D12 | route: ที่เหลือ | **ไม่แตะ** — `carts/{id}/items`, `payments/sessions`, `orders/{token}/summary`, `admins/{id}/roles` nest ดีแล้ว |
| D13 | Sequencing | **ทำเต็ม scope ตอนนี้** ไม่รอ rf2 |
| D14 | **กฎพหูพจน์** | บังคับ 2 ชั้น: **(ก) module project** — `Admin`->`Admins`, `Cart`->`Carts`, `Checkout`->`Checkouts` (อีก 4 ตัวพหูพจน์อยู่แล้ว); **(ข) sub-namespace/folder** — `Users`, `Roles`, `Items`, `Sessions`, `Permissions` |
| D15 | ที่ **ไม่** บังคับพหูพจน์ | **DB schema** คงเดิม (`shop`/`txn`/`admin`/`merch`/`sec` — rf1 เพิ่ง lock); **type name คงเอกพจน์** ตาม .NET convention (`Users.User`, `Roles.Role`, `Items.Item`) |

## Known cost ที่ต้องรับ (จาก D2+D13) — ตัวเลขจริง ไม่ใช่คำเตือนลอย

roadmap v5 (rf2-rf11) จะ**ลบ**สิ่งที่ spec นี้กำลังจะ rename อยู่หลายก้อน:

| roadmap | จะทำอะไร | ของที่ spec นี้ rename แล้วโดนทิ้ง |
|---------|----------|--------------------------------------|
| **rf2** | แทน RBAC catalog 2 ชุดด้วย `iam.*` (`rf1/design.md:107-108,116-117`) | `AdminRole*`/`AdminPermission*` (39+26 ไฟล์) + `MerchantUserRole*`/`MerchantUserPermission*` — RBAC entity + EF config + repo + ตาราง + permission-key catalog **ทั้งหมด** |
| **rf3** | แทน `PspConnection` ด้วย `cfg.GatewayConfigs` (`rf1/design.md:102`) | `PspConnection`/`PspCode` (32 ไฟล์) |
| **rf6** | แทน `PaymentSessions` ด้วย `Payments`/`PaymentAttempts` (`rf1/design.md:101`) | `PaymentSession*` (41 ไฟล์) |
| **rf5/rf6** | สร้าง `merch.MerchantUserHierarchy` | ต้องเกิดมาด้วยชื่อใหม่ ไม่ใช่ชื่อเดิม |

แปลว่า **ประมาณครึ่งหนึ่งของ 262 ไฟล์เป็นงานที่ roadmap จะลบภายใน 5 spec ถัดไป** — ผู้ใช้ยืนยันรับ cost นี้
แลกกับ repo consistent ทันที ไม่ต้องรอ rf2/rf3/rf6

เพิ่ม: `rf1-schema-reset/design.md:149` เพิ่ง lock naming rule (`Producer -> MerchantUser`) เมื่อ 2026-07-12 —
spec นี้ต้อง **amend กฎนั้นอย่างเป็นทางการ** ไม่ปล่อยให้ canon สองฉบับขัดกันเงียบ ๆ

## Traps — design ต้องปิดให้ครบ

**T1 — ย้าย provision-merchant ออกจาก `admins` group = ตก 2 ชั้น security เงียบ ๆ (D9)**
`Program.cs:759` `var admin = api.MapGroup("/admins").AddEndpointFilter<AdminCsrfFilter>();`
`Program.cs:832,877` แขวนอยู่ใต้ group นี้ — ย้ายไป `/api/v1/merchants` แล้วจะ:
1. **หลุด `AdminCsrfFilter`** (bind ที่ group ไม่ใช่ per-endpoint) -> CSRF regression
2. **หลุด admin CORS policy** — `CorsExtensions.cs:79` เลือก policy จาก path ตรง ๆ:
   `Path.StartsWithSegments("/api/v1/admins") ? AdminPolicyName : default` -> ตกไปกิน default
   (merchant-user) policy ที่ origin allowlist คนละชุด
design ต้องระบุ re-binding ชัด; `api-route-scheme` REQ-3.3/3.7 ("filter membership เดิมห้ามขยับ") บังคับอยู่แล้ว

**T2 — route shadow (D9+D10)**
`GET /api/v1/merchants/{code}` โดย `{code}` **ไม่มี constraint** vs `/merchants/users/...`
literal ชนะ param ใน ASP.NET routing ก็จริง แต่ design ต้องใส่ constraint บน `{code}` (slug regex) ไม่พึ่ง precedence เงียบ ๆ

**T3 — Google OAuth redirect URI = external contract (D8)**
`MerchantUserOidcOptions.cs:20` `CallbackPath = "/api/v1/merchant-users/auth/callback"`
ลงทะเบียนไว้ที่ Google Console (นอก repo) — deploy โดยไม่แก้ authorized redirect URI = **login แตกทันที**
ต้องเป็น operator step ใน tasks.md + ผ่าน staging ก่อน prod (Deploy rules) — ทั้ง admin และ merchant-user OIDC

**T4 — EF migration เก็บชื่อ CLR type เป็น string literal**
7 ไฟล์ใต้ `Migrations/` (`InitialSchema`, `SecurityObjects`, `SeedData` + designer + `PolDbContextModelSnapshot`)
มี `modelBuilder.Entity("Merchants.Domain.MerchantUser", ...)` ตรงตัว — เปลี่ยน namespace/class โดยไม่แก้ =
EF เห็น pending model changes ทันที (trap เดียวกับ PR #68)
cutover = **rewrite migration history + reset DB** (`docker compose down -v`) ตาม precedent rf1/PR #68/#69 — ไม่มี transfer migration

**T5 — outbox `Type` column ผูกกับ CLR simple name (D6)**
`OutboxDispatcher.cs:144` `[nameof(MerchantUserRegistrationSubmitted)] = typeof(...)`
rename record = key เปลี่ยน = แถว outbox จาก binary เก่า resolve ไม่ออก -> `InvalidOperationException`
DB reset = ไม่มี in-flight (pre-prod) แต่ design ต้องเขียนว่า cutover ต้อง **drain/reset ไม่ใช่ rolling deploy**

**T6 — arch guard fail-closed + amend spec เดิม (D8)**
`tests/Hosts.Tests/RouteSchemeConventionTests.cs:49` regex allowlist 9 area — `merchants` ไม่อยู่ในนั้น
ต้อง **amend `.ai/specs/api-route-scheme/requirements.md` REQ-2.1 + design mapping table** ก่อน/พร้อมแก้ regex
ห้ามแก้ test ให้ผ่านเฉย ๆ

**T7 — ambiguity ระเบิดจาก D4+D5 (ตัด prefix หมด ทุกโมดูล)**
หลัง nest จะมี `Users.User`, `Users.Session`, `Users.Status`, `Roles.Role`, `Items.Item`, `Sessions.Session`
ชื่อซ้ำข้ามโมดูล (Admin.Users.Session vs Merchants.Users.Session vs Payments.Sessions.Session) และข้ามชั้น
design ต้องล็อก **using/alias discipline เป็นกฎเดียวทั้ง repo** (fully-qualified หรือ alias pattern ตายตัว)
ไม่ปล่อยให้แต่ละไฟล์ mitigate compile error ตามใจ — นี่คือความเสี่ยงอันดับ 1 ของ D4

**T8 — permission key + auth scheme เป็น persisted/wire string (D6)**
`merchant_user.roles.view|manage`, `merchant_user.user.roles` (`MerchantUserPermissions.cs:25-27`) seed ใน
migration `20260711142519_SeedData` และ FE ใช้ตรวจสิทธิ์; auth scheme `"MerchantUserSession"`
(`MerchantUserSessionAuthenticationHandler.cs:27`) โผล่ใน Scalar security scheme + tests; ฝั่ง Admin มีชุดคู่ขนาน
ทั้งหมด breaking ฝั่ง FE -> design ต้องมีหัวข้อ FE migration + อัปเดต `.ai/specs/rf1-schema-reset/FE-MIGRATION.md`

**T9 — `Carts.Domain.Items` มี type เดียว (D2 ผลข้างเคียง)**
โมดูล data-plane (Cart/Orders/Products/Checkout) เป็น single-noun อยู่แล้ว — nest แล้วได้ folder ที่มี type เดียว
(`Carts.Domain/Items/Item.cs`) design ต้องตัดสินว่ายอมรับ (uniformity) หรือกำหนดเกณฑ์ขั้นต่ำว่าเมื่อไหร่ถึง nest

**T10 — rename module project = แตะ build/solution ทั้งระบบ (D14ก)**
`pol-core.slnx` (40 project) ถือ `<Folder Name="/src/Modules/Admin/">` + path csproj ตรงตัว
`Admin`->`Admins`, `Cart`->`Carts`, `Checkout`->`Checkouts` แตะ:
- **9 module csproj** (3 layer x 3 module) + **3 test csproj** (`Admin.Tests`->`Admins.Tests` ฯลฯ)
- ชื่อ folder จริงบนดิสก์ (`src/Modules/Admin/` -> `Admins/`) -> ต้อง `git mv` ไม่ใช่ลบ+สร้าง (ไม่งั้น history ขาด)
- `AssemblyName`/root namespace -> `using Admin.Domain;` ทั้ง repo เปลี่ยนเป็น `using Admins.Domain;`
- `Dockerfile` + `.github/workflows/*` ถ้ามี path csproj ตรงตัว -> ต้อง grep ยืนยัน
- `SchemaNames.Admin = "admin"` **คงเดิม** (D15) — จุดที่ชื่อ project กับ schema จะไม่ตรงกันโดยตั้งใจ ต้องเขียน rationale ไว้ใน design ไม่งั้นคนอ่านทีหลังจะ "แก้ให้ตรง"

**T11 — โฟลเดอร์ตาย `Identity/`, `Producer/`, `Tenant/`**
ไม่อยู่ใน `pol-core.slnx` แล้ว เหลือแค่ `obj/` build artifact ค้างบนดิสก์ — ลบทิ้งได้ในงานนี้ (cleanup)
แต่ต้องแยกเป็น commit ของตัวเอง ไม่ปนกับ rename sweep

## Plan

1. สร้าง `.ai/specs/hierarchical-naming/`
2. **`/spec-design`** (Design-First) — design.md ต้องปิด T1-T11 โดยเฉพาะ:
   - mapping table เก่า->ใหม่ **ครบทุกแกน** (namespace+type ทุกโมดูล / route / DB table / permission key / auth scheme / outbox contract)
   - **ambiguity policy** (T7) — กฎ using/alias เดียวทั้ง repo
   - filter + CORS re-binding หลังย้าย provision-merchant (T1)
   - cutover: rewrite migration history + DB reset, ไม่ rolling (T4/T5)
   - operator step: Google Console redirect URI ทั้ง 2 OIDC scheme (T3)
   - amendment ที่ต้องทำกับ `api-route-scheme` REQ-2.1 และ `rf1-schema-reset` naming rule §149
   - เกณฑ์ nest ขั้นต่ำสำหรับโมดูล single-type (T9)
   - กฎพหูพจน์ D14/D15 เขียนเป็น convention ถาวรลง `.ai/shared/ARCHITECTURE.md` §Naming Conventions (ตอนนี้มีแค่ "type/interface: PascalCase" — ไม่มีกฎ plural/nesting เลย จึงเกิดของปนกันตั้งแต่แรก)
3. Gate: user review design -> `/spec-requirements` (EARS behavior-preserving + guard ใหม่) -> gate -> `/spec-tasks`
4. Implement — คาด **1 task ต่อโมดูล** + task แยกสำหรับ migrations / Hosts+route / contracts+outbox / docs+FE
   (แนะนำแตก PR ต่อโมดูลบน branch เดียว ไม่ใช่ PR เดียว 262 ไฟล์)

## Verification (จะไปอยู่ใน tasks.md)

- `dotnet build` 0 error หลัง `pol-core.slnx` โหลด 40 project ครบ (T10 — csproj/folder rename พังเงียบได้)
- `dotnet test` เขียวทุก project (Admins/Merchants/Carts/Checkouts/Orders/Payments/Products/Hosts/Architecture/Integration.Tests)
- `RouteSchemeConventionTests` ผ่านด้วย regex ที่ **มี `merchants` ไม่มี `merchant-users`** (fail-closed ยังคง fail-closed)
- fresh DB: `docker compose down -v` + `dotnet ef database update` -> **ไม่มี pending model changes** (T4)
- E2E บน dev: admin login + merchant-user login ผ่าน Google ที่ callback path ใหม่ (T3) — restart API/scalar ก่อน (dev binary ค้าง)
- outbox: publish event ที่ rename แล้ว worker consume ได้จริง (T5)
- grep gate: `\b(MerchantUser|PlatformUser|AdminRole|PaymentSession|CartItem|CheckoutSession|PspConnection)\b` ใน `src/ tests/` เหลือศูนย์ (ยกเว้น comment อ้างประวัติ)
- RLS matrix test ยังเขียว (rename แตะ EF config + `sec.fn_merchant_predicate` ที่อ้างชื่อตาราง)
