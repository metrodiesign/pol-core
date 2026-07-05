> Canonical source for ALL agents (Claude loads via .claude/rules stub; Codex/OpenCode/Pi read directly).
> แก้ที่นี่ที่เดียว — single source of truth.

# Technology Stack

> Stack-neutral framework rules อยู่ด้านล่าง (universal). **Concrete stack ของ pol-core อยู่ที่นี่** —
> stack ถูกตัดสินแล้ว, adopt มัน อย่าแตกแนว.

## Stack (pol-core) — ตัดสินแล้ว, pin version

**สถาปัตยกรรม:** Modular Monolith ตามแนว **Clean Architecture + CQRS** (command/query แยกชัด, dependency ชี้เข้าใน domain)

| สิ่ง | version (pin) | หมายเหตุ |
|---|---|---|
| .NET / ASP.NET Core | **10** (LTS) | runtime + web |
| C# | **14** | เปิด nullable + type checking เข้มสุด |
| EF Core | **10** (align กับ .NET 10) | ORM |
| SQL Server | **2025 Standard** | เก็บ UTC เสมอ (datetime2; field/column **ไม่ใส่** suffix `Utc` — ตั้งชื่อ `CreatedAt`/`UpdatedAt`/`OccurredAt`/...) · schema เดียว `producer`; admin = control-plane tables ในนั้น (ไม่มี RLS predicate, pol_admin only), producer = data-plane (RLS) |
| martinothamar/Mediator | **3.0.1** | in-process command/query/handler + pipeline behaviors (3.0.0 ไม่ publish) |
| Omise API | `apiVersion` **2019-05-29** | external PSP API (ต่อ tenant, จาก config) |

> **Compat verified** (spike 2026-06-21, [docs/spikes/2026-06-21-stack-compatibility.md](../../docs/spikes/2026-06-21-stack-compatibility.md)):
> ทั้ง chain ทำงาน end-to-end บน SQL Server 2025 RTM-CU5. **Dependency-audit caveat:** `Mediator.SourceGenerator`
> ดึง `Scriban` 6.2.0 (critical/high) + `System.Security.Cryptography.Xml` 9.0.0 (high) แบบ transitive build-time
> (`PrivateAssets=all`, ไม่ ship runtime) — CI audit จะ flag, ต้อง suppress รายตัวพร้อมเหตุผล ห้าม force-downgrade core dep.

> exact version (รวม patch) pin ที่ `Directory.Packages.props` / `.csproj` + commit lock — ห้าม floating (`*`/`latest`).
> ขึ้น major ใหม่ = ต้องมีเหตุผลบันทึก + ขออนุมัติก่อน (ดู Dependency rules).

**Commands / layout / EF / Mediator idioms เต็ม:** [stack/dotnet.md](stack/dotnet.md) ·
gate: `SDD_TYPECHECK_CMD="dotnet build -warnaserror"` · `SDD_TEST_CMD="dotnet test"`

**martinothamar/Mediator** (source-generated, compile-time wiring, AOT-friendly — ไม่ reflection/assembly-scan):
- CQRS: `ICommand<,>` / `IQuery<,>` (แยก command/query); cross-module event: `INotification`
- handler: `IRequestHandler<,>` / `INotificationHandler<>`; cross-cutting: `IPipelineBehavior<,>` (เช่น `IdempotencyBehavior`)
- `Handle` คืน `ValueTask<T>` · `AddMediator(...)` (gen ให้) · pipeline behaviors เพิ่มเอง
- **lifetime:** `IMediator` Singleton ได้ แต่ handler/pipeline ที่พึ่ง `DbContext` ต้อง **Scoped** (หรือ `IDbContextFactory`) — กัน captive dependency; `ValidateScopes=true` + DI validation test
- ได้ error ตอน **build** ถ้าไม่มี handler ของ request

**Money:** มาตรฐาน (ตัดสิน 2026-07-05) = `Money { Amount: DECIMAL(19,4), Currency: ISO4217 }` **ทุกชั้น** — domain, persistence (SQL Server `DECIMAL(19,4)`), wire (แนะนำ JSON string กัน IEEE754 double — carrier สุดท้ายรอ ADR) · **ห้าม float/double เด็ดขาด** · as-built ปัจจุบันยังเป็น `Money { MinorUnits: long }` (bigint) = legacy จนกว่า migration (gap ข้อ 22 + ADR 16 ใน `docs/reference/platform-modules.md`); Orders verify amount+currency ตอนรับ `PaymentPaid` (ดู [ARCHITECTURE.md](ARCHITECTURE.md))

**Secret:** PSP key เก็บใน vault (envelope encryption, per-tenant KEK ใน KMS/HSM, key id+version+rotation), write-only, อ่านกลับ mask เสมอ — ไม่ hardcode (ดู [SECURITY_RULES.md](SECURITY_RULES.md))

### Naming (หลักสำคัญ — ตารางเต็มใน `docs/reference/payment-orchestration-modules.md`)

- **C# identifier ↔ entity ↔ table ↔ column สะกดตรงกัน (PascalCase)** → EF Core map ตรงไม่ต้อง alias
- Acronym ≥3 ตัว = PascalCase → ใช้ **`Psp`** ตลอด (ไม่ใช่ `PSP`): `PspConnection`, `IPspAdapter`, `pspConnectionId`
- `2C2P` ขึ้นต้นด้วยเลข เป็น identifier ตรงไม่ได้ → enum member **`TwoCTwoP`** · `Omise` ใช้ตรงได้
- async method ลงท้าย `Async` · interface `I` นำหน้า · private field `_camelCase` · PK = `{Entity}Id` · FK = `{Navigation}Id`
- **Wire format คนละ convention:** JSON property = **camelCase** (`JsonNamingPolicy.CamelCase` ตั้งครั้งเดียว) · JWT/OIDC claim = ตามสเปก (`iss`/`aud`/`sub`/`hd`/`email_verified`)
- **ค่า code string เสถียร** (แยกจากชื่อ enum): `"2c2p"`/`"omise"` · `"card"`/`"promptpay"`/`"installment"`
- **ค่าจาก PSP ภายนอกคงรูปเดิมเสมอ:** Omise source types (`installment_kbank`...), `authorize_uri`, `return_uri`, event `charge.complete` — ห้ามเปลี่ยน
- canonical entities: `Tenant` · `PspConnection` · `VaultSecret` · `PaymentSession` · `AdminAccount` · `AdminTenantAssignment` · `AdminAccountAudit` · `AdminSession` · `AdminAuthAudit` (admin BFF session + auth-event audit, control-plane) · **Producer module** (rebuilt 2026-06-28 from the removed Identity module; reshaped to Admin parity 2026-06-29): producer actor = **`ProducerAccount`** (control-plane; person/license data lives on the account) + `ProducerTenantAssignment` (1 tenant per account) + `ExternalLogin`/`RegistrationAudit` + BFF session `ProducerSession`/`ProducerAuthAudit` + RBAC `ProducerRole`/`ProducerRolePermission`/`ProducerRoleAssignment`/`ProducerPermission`(s) (control-plane). Registration ticket = stateless signed Data Protection token (`RegistrationTickets` table dropped 2026-07-01). NOTE: the entity was renamed from the earlier `TenantUser`, but wire routes/contracts still say `tenant-user(s)` (e.g. `/admin/tenant-users/{subject}/approve`, `TenantUserRegistrationSubmitted`) — do not rename those.
- ถ้าจะใช้ snake_case ใน DB → ตั้ง global convention ครั้งเดียว (`UseSnakeCaseNamingConvention()`) อย่าสลับมือทีละตาราง
- **target entities (normative target — ยังไม่ใช่ as-built):** `Payment` · `PaymentAttempt` · `WebhookDelivery` (+ read model `Transaction`) และ canonical payment status 7 ค่า — นิยามใน `docs/reference/payment-orchestration-modules.md` ภาค 8 + `docs/reference/platform-modules.md`; `PaymentSession` + `PaymentStatus` 4 ค่า ยังเป็นชื่อจริงในโค้ดจนกว่า migration (rename ผ่าน ADR)
- **API conventions (target):** base path `/api/{surface}/v1` (ตัดสินแล้ว 2026-07-05 — route ปัจจุบันไม่มี version = legacy) · inbound `Idempotency-Key` · `ETag`/`If-Match` · RFC 9457 + stable `code` · correlation ids — สเปกเต็ม: `docs/reference/platform-modules.md` ส่วน "เป้าหมายเชิง API ระดับแพลตฟอร์ม"; **SFS (`docs/reference/search-filter-sort.md`) ยังเป็น convention บังคับของ list endpoint จนกว่ามี ADR เรื่อง cursor**

---

## (universal framework rules)

> Stack-neutral. The rules below are universal; concrete picks สำหรับ pol-core อยู่ด้านบน.

## Languages & Runtimes

- ใช้ภาษาและ runtime ที่โปรเจกต์ตั้งไว้แล้ว — adopt the established stack, อย่าแตกแนว
- เลือก statically-typed language เมื่อเริ่มใหม่ และเปิด type checking ให้เข้มที่สุดเท่าที่
  ภาษานั้นมี; กำหนด type ของ data/interface ให้ชัดเจน, เลี่ยง escape hatch แบบ dynamic/`any`
- ห้ามเพิ่มภาษา/runtime ใหม่เข้าโปรเจกต์โดยไม่มีเหตุผลที่บันทึกไว้ + ขออนุมัติก่อน

## Frameworks & Core Libraries

- ใช้ framework ที่โปรเจกต์ใช้อยู่แล้ว — ทำตาม convention ของ framework นั้น อย่าผสมหลายตัว
- การเพิ่ม library ใหม่ต้องมีเหตุผลที่บันทึกไว้ + ขออนุมัติก่อน (ดู Dependency rules ด้านล่าง);
  การ approve PR ที่บันทึกการเพิ่มนั้น = การอนุมัติ
- stack-specific guidance (UI framework, styling system, test-runner idioms) อยู่ใน profile
  เสริมแบบ optional ใต้ `.ai/shared/stack/` — เพิ่มไฟล์ของ stack ตัวเองเมื่อต้องการ;
  framework ไม่ bundle profile ใดมาให้โดย default

## Data Layer

- มี DB / backend ก็ต่อเมื่อโปรเจกต์ใช้จริง — ถ้าไม่มี ก็ใช้ข้อมูล mock แบบ typed แยกไฟล์
  พร้อม type ชัดเจน อย่าฝัง logic ไว้กับข้อมูล

## Tooling

- โปรเจกต์เป็นผู้ประกาศคำสั่ง typecheck / test / build ของตัวเอง — ไม่มี default ตายตัว
- task gate (`.ai/bin/gate-task.sh`) อ่านคำสั่งจาก env: `SDD_TYPECHECK_CMD` และ `SDD_TEST_CMD`
  (สำหรับ stack ใดก็ได้); สำหรับ Node ที่มี `package.json` จะ auto-detect script `typecheck` /
  `test` ให้เอง — ถ้าไม่มีทั้ง env และ script จะข้าม code-green check แล้วเหลือเพียง Evidence gate
- ตั้งค่า/รัน dev ด้วยคำสั่ง setup และ dev ของโปรเจกต์เอง

## Hard Constraints

- ห้าม hardcode secret ทุกชนิด — อ่านจาก environment variable หรือ secret manager เท่านั้น
  (ดู [SECURITY_RULES.md](SECURITY_RULES.md))
- ห้าม pin เวอร์ชันแบบ floating (`*` / `latest`) บน prod dependency; commit lock file เสมอ
  เมื่อใช้ package manager
- accessibility + semantic markup เป็นหลักการ ไม่ใช่ option: ทุกองค์ประกอบที่สื่อความต้องมี
  ป้ายกำกับ/`alt`, ใช้ semantic element, นำทางด้วยคีย์บอร์ดได้, contrast ผ่านเกณฑ์

## Rule

ใช้ stack ที่โปรเจกต์ตั้งไว้แล้วก่อนทางเลือกอื่น. ห้ามเพิ่ม library/ภาษา/runtime ใหม่
โดยไม่ระบุเหตุผลและขออนุมัติก่อน.
