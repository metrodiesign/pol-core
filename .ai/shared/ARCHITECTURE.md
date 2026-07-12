> Canonical source for ALL agents (Claude loads via .claude/rules stub; Codex/OpenCode/Pi read directly).
> แก้ที่นี่ที่เดียว — single source of truth.

# Project Structure

## Folder Layout

โครงสร้างจริงของ repo นี้ (ตัว framework เอง) เป็นตัวอย่าง concrete ของการแยก
operating layer ที่ vendor-neutral ออกจาก per-agent adapter:

```
.ai/                  # operating layer ที่ใช้ร่วมทุก agent (durable source of truth)
  shared/             # มาตรฐาน + protocol ที่อ่านได้ทุก agent (PROJECT_CONTEXT, CODING_STANDARDS,
                      #   ARCHITECTURE, LESSONS, TASK_PROTOCOL, EARS, REVIEW/TESTING/SECURITY/...)
  bin/                # check engine จริง (gate-task.sh, check-secrets.sh, check-destructive.sh, ...)
  roles/              # นิยาม role กลาง (spec-architect, bug-investigator, pbt-runner)
  workflows/          # คู่มือ flow ต่อชนิดงาน (feature, bug-fix, code-review, ...)
  templates/          # template ของ artifact (handoff note, review report, task brief, ...)
  agents/             # per-agent adapter map (claude/, codex/, opencode/, pi/)
.claude/              # Claude Code adapter — agents/, commands/, hooks/, rules/ (stub), skills/,
                      #   specs/, settings.json
.codex/               # Codex adapter — agents/, hooks/, config.toml
.opencode/            # OpenCode adapter — agents/, commands/, plugins/
.agents/              # adapter ร่วม (skills/)
.githooks/            # enforcement floor (Tier 1): pre-commit, pre-push
.github/              # CI workflows + pull_request_template.md
scripts/              # automation (pane-loop, cost/trace tooling, spec-state, ...)
docs/                 # คู่มือผู้ใช้ของ framework
retrospectives/       # บันทึก retro รายเดือน
.ai/specs/<feature-name>/   # spec artifact ต่อ feature: requirements.md, design.md, tasks.md
                                #   (+ .github-sync.json sidecar เมื่อ sync แล้ว)
```

> layout นี้เป็นตัวแทนหลัก ไม่ exhaustive — ground truth คือ `ls` จริง;
> /spec-retro มีขั้น steering sync คอยเทียบให้ตรง

## Application structure (per project)

`.ai/` คือ operating layer ของ framework ไม่ใช่ของแอป — แต่ละ project ที่ใช้ framework นี้
จัดวาง source ของตัวเองอย่างไรก็ได้ตาม stack ที่เลือก โดยยึด PRINCIPLE ต่อไปนี้
(ไม่ผูกกับ framework/ภาษาใดภาษาหนึ่ง):

- แยก pure logic ออกจาก presentation — logic คำนวณ/validate/transform อยู่คนละชั้นกับ
  ส่วน UI; ส่วน UI เรียกใช้ ไม่ฝังสูตรไว้ในตัว view
- co-locate unit test ไว้ข้าง logic ที่มันทดสอบ (test อยู่ติดกับโค้ดที่รับผิดชอบ)
- config/design token มี single source ที่เดียว — เรียกผ่าน semantic reference ไม่ทำซ้ำค่าดิบ
- จัด import เป็นชั้น: external ก่อน → internal absolute → relative
- naming convention ชัดและคงเส้นคงวาทั้ง project (ดู Naming Conventions ด้านล่าง)

`.github-sync.json` ใน `.ai/specs/<feature>/` = sidecar manifest ของ `/spec-sync-github`
(link map issue<->task) — commit เข้า repo, เฉพาะคำสั่ง sync เขียน; ห้ามแก้มือ,
ห้ามใส่ link ลง tasks.md

## pol-core application architecture (payment platform)

> สถาปัตยกรรมของผลิตภัณฑ์ — source code อยู่ที่ `src/` แล้ว; ส่วนนี้คือ target shape ที่โค้ดต้องตาม.
> รายละเอียดเต็ม: `docs/reference/payment-orchestration-modules.md` · module map + สถานะ as-built:
> `docs/reference/platform-modules.md` · product canon: [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md)
>
> **Target API design (normative, รับเข้า 2026-07-05)** อยู่ในสองไฟล์นั้น: platform-modules.md
> (ส่วน "เป้าหมายเชิง API ระดับแพลตฟอร์ม" + "โมเดลเป้าหมายเชิง API" ต่อโมดูล) และ
> payment-orchestration-modules.md ภาค 8 (Payment/PaymentAttempt/WebhookDelivery/Routing) —
> โค้ดปัจจุบันยังไม่ตาม target หลายจุด: ช่องว่างดู platform-modules.md "ช่องว่างเทียบเป้าหมาย" ข้อ 16-22
>
> **API path scheme (as-built, spec `api-route-scheme` 2026-07-05):** `/api/v1/{area}` — version-first global
> (`v1` เดียวทั้ง API), segment ที่สอง = domain area (9 area plural: `products`/`carts`/`checkouts`/`orders`/
> `payments`/`webhooks`/`reports`/`admins`/`merchant-users`), audience บังคับ per-endpoint ผ่าน `RequireAuthorization`
> (ไม่อยู่ใน path). infra (`/health/live`,`/health/ready`,`/openapi/*`,`/scalar`) อยู่นอก `/api/v1`. big-bang —
> route flat เดิมถูกลบ (ไม่ alias); supersede มาตรฐานเดิมแบบ surface-first (audience นำหน้า version).

**รูปทรง:** Modular Monolith ตามแนว **Clean Architecture + CQRS** — 1 deployable backend, แยกเป็นโมดูล,
dependency ชี้เข้า domain, command/query แยกผ่าน Mediator (`ICommand`/`IQuery`).

แยก **2 ระนาบที่ขาดจากกัน** — control plane ไม่แตะเส้นทางเงิน, data plane จัดการ request การจ่าย + สถานะ (ไม่ใช่ตัวเงิน):

```
ช่องทางบริษัทในเครือ
   → control plane: pol-tenant SPA (app#1) · pol-admin SPA (app#2)   ← 2 browser SPA คนละ deploy, เรียก Backend API ตัวเดียว
   → platform core: Session layer (Create / Return / Webhook) · Engine (Method router, Credential vault,
                    Retry & dunning, Reconciliation, Idempotency store)
   → PSP adapter: IPspAdapter (2C2P · Omise/Opn) — normalize เป็นสัญญาเดียว
   → PSP (ใน PCI scope) → เงิน settle เข้าบัญชี merchant ของบริษัทโดยตรง (นอกแพลตฟอร์ม)
```

**5 โมดูล (in-process, คุยผ่าน Mediator — ไม่อ้างถึงกันตรง):**
Products → Cart → Checkout → Orders → Payments. cross-module event ใช้ `INotification` (เช่น `PaymentPaid`).

**Flow แกน (happy path):** Checkout ล็อก 1 ช่องทางจ่าย → สร้าง Order (`PendingPayment`, ยังไม่แตะ PSP) →
Orders ส่งลิงก์หน้าสรุป (background) → ลูกค้าเปิด Payments → กดยืนยัน → **Payments แตะ PSP ครั้งแรก** (สร้าง `paymentUri`) →
redirect → ลูกค้าจ่าย → **webhook = source of truth** (verify + idempotent + fetch-to-confirm) → emit `PaymentPaid` →
Orders → Paid. จบ ไม่มี issuance.

**Seam ที่ต้องระวัง (Payments ↔ Orders):**
- `PaymentPaid.Amount` เป็น `Money` (SharedKernel) ใช้ร่วมทั้งสองโมดูลแล้ว — ห้ามถอยกลับไป scalar/decimal ที่ seam
- Orders รับ `PaymentPaid` ต้อง **verify amount/currency** ไม่ใช่แค่ id (กันจ่ายไม่ครบ/สกุลผิด) — ทำแล้วใน `Order.MarkPaid`
- Orders จับคู่ order ด้วย **`PaymentPaid.OrderId`** (PR #44, spec `bugfix-order-paid-link`) — `Order.PaymentSessionId` เป็น legacy ไม่มี production writer ห้ามใช้เป็น join key

**Cross-cutting (บังคับทั้งระบบ — security detail: [SECURITY_RULES.md](SECURITY_RULES.md) Product security):**
- Multi-merchant isolation — **floor = SQL Server native RLS + `SESSION_CONTEXT('MerchantId', 'UserId')`** ต่อ request (ไม่พึ่ง app code);
  EF global query filter = ชั้นสะดวกเสริมไม่ใช่ floor. ban raw SQL / `IgnoreQueryFilters` ข้าม merchant + test leak. backend ร่วมกัน
- แยก backend authz scope ให้ขาด — endpoint admin (cross-merchant/approve/config) เรียกผ่าน session ฝั่ง merchant-user ไม่ได้;
  admin cross-merchant bypass RLS ผ่าน **DB principal แยก** เท่านั้น + reason/correlation id → audit
- **Scoped-admin isolation = RLS floor จริงแล้ว (rf1 REQ-3.2/3.3 — เดิมเป็น app-layer exception ตาม admin-actor-rename REQ-7.4):**
  `pol_admin` ถูกถอดออกจาก `pol_rls_bypass` (2026-07-12) — `sec.fn_merchant_predicate` เช็ค tier ของ `admin.PlatformUsers`
  เองใน DB: Super เห็นทุกแถว, Scoped เห็นเฉพาะ merchant ที่มีแถวใน `admin.PlatformMerchantAccess` (fail-closed —
  ไม่มีแถวเลย = เห็นศูนย์ ไม่ใช่เห็นหมด). seam แอป `IAdminQuery` (ฝัง `WHERE MerchantId ∈ accessible`; Super = unrestricted)
  **ยังอยู่เป็น belt-and-braces เสริม** ไม่ใช่ floor เดี่ยวอีกต่อไป — Architecture.Tests ห้าม handler อื่นส่ง cross-merchant
  query ตรง + leak/bypass test = compensating control ชั้นสอง
- Credential vault — **envelope encryption (per-merchant KEK ใน KMS/HSM, DEK ต่อ secret)**, key id+version + rotation runbook; secret write-only, อ่านกลับ mask เสมอ
- Identity — Google SSO ทำที่ชั้น auth, **ทั้ง 2 console โมเดลเดียวกันแล้ว (rf1 — ถอด Bearer ทิ้งทั้งระบบ)**: admin console และ
  merchant-user (ตัวแทน) console ต่างมี **server-side OIDC BFF** ของตัวเอง (Authorization Code + PKCE, confidential client),
  คนละ scheme/cookie/DP-purpose แยกขาด — ไม่มี Google id-token Bearer เหลือแล้ว (เดิม tenant SPA ใช้ Bearer audience `tenant`,
  ถอดพร้อม policy `tenant` ทั้งก้อน). Admin: scheme `Google`, opaque session cookie `__Host-adm_session` (เก็บแค่ SHA-256
  hash), rotation + reuse-detection + instant revoke, CSRF double-submit, RBAC resolve สดต่อ request (**retire
  id-token-as-bearer audience 2026-06-24**). Merchant-user: scheme `MerchantUserGoogle` (เดิม `ProducerGoogle`), cookie
  `__Host-mch_session` + csrf `mch_csrf` (เดิม `__Host-prd_session`/`prd_csrf`), กลไกเดียวกัน, policy `merchant-user`
  **single-scheme** (เดิม dual-scheme `producer` = ProducerSession OR tenant Bearer). actor = **`MerchantUser`** (เดิม
  `ProducerAccount`; ตาราง `MerchantUsers` — ดูดซับ `ProducerTenantAssignments` เดิมเป็นคอลัมน์ `MerchantId` บนตัว account
  ตรง) + `ExternalLogins`/`RegistrationAudits` (ชื่อเดิม) + session `MerchantUserSessions`/`MerchantAuthAudits` (เดิม
  `ProducerSessions`/`ProducerAuthAudits`) + RBAC catalog เดิม rename ทั้งชุด (รายตารางดู
  [rf1-schema-reset design.md](../specs/rf1-schema-reset/design.md#data-models--interfaces)) + permission key
  `merchant_user.approve`/`.reject` (เดิม `producer.*`). register ยัง anonymous ticket-gated (signed Data Protection
  token) → admin approve/reject เหมือนเดิม. **module Identity ถูกลบ 2026-06-23 → rebuild เป็น Producer 2026-06-28 → รวมกับ
  Tenant เป็น Merchants module เดียว (rf1, 2026-07-12).** คงเหลือ **`PlatformUser`\*** (เดิม `AdminAccount`\*) + BFF
  session tables (`PlatformUserSessions`/`PlatformAuthAudits`/`PlatformUserAudits`, เดิม
  `AdminSessions`/`AdminAuthAudits`/`AdminAccountAudits`) + `DataProtectionKeys` = control-plane (ไม่มี RLS predicate,
  pol_admin only) ใน **Admin module**, schema `admin`; MerchantUser identity/session/RBAC ตารางข้างต้นอยู่ schema
  `merch` — **คนละ schema กันแล้ว** (เดิมทั้งคู่ schema เดียว `producer` ก่อน rf1). schema ไม่ใช่เส้นแบ่ง RLS — floor บังคับ
  รายตาราง (`merch.Merchants` self-row + `merch.VaultSecrets`/`VaultRevealAudits` อยู่ใต้ policy แม้อยู่ schema เดียวกับ
  ตารางข้างบนที่ไม่อยู่ใต้ policy)
- RBAC catalog — **rf2 (2026-07-13, spec `rf2-iam-rbac`)**: catalog ที่เดิมซ้ำ 2 ชุดต่อ console (schema `admin` + `merch`,
  16 keys/6 groups + 7 keys/3 groups) ยุบเป็น **catalog กลางเดียว module `Iam` schema `iam`** — 4 tables
  `iam.PermissionGroups`/`Permissions`/`Roles`/`RolePermissions` (PK = dot-notation key string). Vocabulary = **20 keys /
  8 groups** โดย `PermissionGroups.Scope ∈ {Platform, Merchant}` ทุก key สืบทอด side จาก group → assign/grant ข้าม side
  fail-closed by construction (ปิด cross-side grant hole ที่ 2 catalog เดิม detect ไม่ได้). Seed **4 roles**: `platform_admin`
  (13 platform keys) / `platform_auditor` (4) / `merchant_manager` (7 merchant keys) / `merchant_staff` (4); anchor ปิด/ลบ
  ไม่ได้ = `platform_admin` + `merchant_manager` (แทน anchor เดิม `super_admin`/`merchant_owner`). `Roles.MerchantId` (NULL =
  shared/seed, มีค่า = custom ของ merchant นั้น) ปิด wart เดิมที่ merchant custom role รั่วข้าม merchant. คงต่อ side แค่
  assignment 2 ตาราง (`admin.RoleAssignments`/`merch.RoleAssignments`, FK `RoleId`→`iam.Roles`). `RequirePermission` +
  boot parity guard side-aware เหลือกลไกเดียว (`Api.Iam`); resolve permission สดต่อ request จาก DB (union ของ role Active),
  fail-closed 403. `iam.*` อยู่นอก RLS (REQ-9.2 — resolve ระหว่าง authenticate, app-layer scoped read เป็น floor). แกน role
  (action) กับ Tier/RLS (visibility) ยัง **orthogonal** — งาน visibility เป็น rf6. รายละเอียด: `.ai/specs/rf2-iam-rbac/`
- Maker-checker (approve merchant, เปลี่ยน routing, แก้ allowlist) · idempotency (multi-key + outbox) · audit log (append-only + tamper-evident)
- Provisioning = **saga** (DB กับ vault คนละ store, ไม่มี distributed tx): `PendingProvisioning` → write DB → write vault (idempotency key) → verify → activate ขั้นสุดท้าย → compensation/retry. validate (allowlist+schema) ก่อนเขียน + idempotent ด้วย merchant key. provision merchant ใหม่ = **Super-only ที่ DB floor** (rf1 REQ-3.7 — Scoped INSERT `merch.Merchants` โดน BLOCK, control ใหม่)
- Money — มาตรฐาน (ตัดสิน 2026-07-05, **as-built แล้ว rf1 2026-07-12**) = `Money { Amount: DECIMAL(19,4), Currency: ISO4217 }` **ทุกชั้น** (domain/DB/wire) **ห้าม float/double**; wire = JSON string fixed 4 ตำแหน่ง (กัน IEEE754 double) — รายละเอียดกฎเต็มดู [CODING_STANDARDS.md](CODING_STANDARDS.md); Orders verify amount+currency ตอนรับ `PaymentPaid`

## Naming Conventions

- type/interface: PascalCase
- ไฟล์ logic/data: ตาม convention ของภาษา/stack ที่ project เลือก แต่คงเส้นคงวาทั้ง repo
- ค่าคงที่ที่ export: ตั้งชื่อสื่อความหมาย + มี type ชัด

### Namespace + route naming law (L1-L8, spec `hierarchical-naming`, 2026-07-12)

โครงสร้าง module/namespace ของ backend (C#) ยึดกฎ 8 ข้อนี้ — ที่มาและตัวอย่างเต็มอยู่ที่
[hierarchical-naming design.md](../specs/hierarchical-naming/design.md#the-naming-law-implementers-derive-every-rename-from-it);
สรุปเป็น canon ที่นี่กันไม่ให้ repo drift กลับไปเหมือนก่อนกฎนี้มีอยู่ (สาเหตุเดิมของความไม่คงเส้นคงวา:
project ตัวเอกพจน์ปนพหูพจน์, type คำนำหน้าซ้ำ namespace ตัวเอง, route area หนึ่งเป็น compound noun):

| ID | Law |
|----|-----|
| **L1** | Nesting unit = sub-domain (กลุ่ม type ที่แขวนกับ non-root aggregate เดียวหรือ cross-cutting concern เดียว) ห้าม nest เพื่อความสมมาตรเฉยๆ |
| **L2** | Root aggregate ของ module อยู่ที่ module-root namespace เสมอ — module ห้าม nest ตัวเองซ้ำ, ห้ามสร้าง sub-namespace ไว้เก็บแค่ root |
| **L3** | **Plural** สำหรับ module project + sub-namespace/folder; **Singular** สำหรับชื่อ type |
| **L4** | Prefix drop — type ตัดทุก token ที่ namespace ตัวเองมีอยู่แล้วออก แต่หยุดตรงจุดที่ชื่อที่สั้นลงจะกำกวม (bare verb หรือ framework word) |
| **L5** | Max nesting depth = 2 ชั้นย่อยใต้ layer หนึ่ง ลึกกว่านั้นให้คง compound type name แทน |
| **L6** | Ambiguity policy — แก้ด้วย file-level alias รูปแบบเดียวคงที่ (`using <ModuleSingular><Type> = <Module>.<Layer>.<Sub>.<Type>`) เท่านั้น อยู่แค่ในไฟล์ที่ใช้ ห้าม `GlobalUsings`, ห้าม partial qualification, ห้ามแก้ปัญหาด้วยการเติม prefix กลับเข้า type (จะย้อนกลับไปเป็นปัญหาเดิม) |
| **L7** | DB table qualify ด้วย schema เท่านั้น (SQL มี namespace แค่ชั้นเดียว) — L4 ใช้ได้แค่เท่าที่ schema แยกความกำกวมให้แล้ว ตารางคงคำที่จำเป็นเพื่อไม่ให้ชนกันและยังอ่านออก |
| **L8** | Configuration key, OpenAPI security-scheme id, และ integration-event type name **ไม่ namespace** — เป็น flat external contract, L4 ใช้ไม่ได้ (ไม่มี namespace ให้อิง) การเปลี่ยนชื่อพวกนี้คือ contract change ต้องรีวิวแยกเป็นของตัวเอง ห้ามเปลี่ยนเป็นผลพลอยได้จากการ sweep เปลี่ยนชื่ออื่น |

L8 สำคัญที่สุด — ไม่มีกฎนี้ find-and-replace ทั่ว repo อาจไปแก้ configuration key ที่ผูกกับ security
control (เช่น open-redirect allowlist) แบบเงียบๆ โดยไม่มี test จับได้เลย.

## Import Ordering

1. external (dependency ของภายนอก)
2. internal absolute (โมดูลภายใน project)
3. relative (`./...`)

## Architectural Patterns

- logic คำนวณ/validate แยกเป็นชั้นของตัวเอง — ส่วน UI เรียกใช้ ไม่ฝังสูตรไว้ในตัว view
- data แยกจากตัว presentation — ส่งผ่าน props หรือ import โดยตรง ไม่ inline ก้อนใหญ่ในไฟล์ view
- design token อยู่ที่เดียว — เรียกผ่าน semantic reference
- ถ้า project มี UI: องค์ประกอบ interactive มี state ครบ (default/hover/focus/active/disabled)
  และ accessible เป็น principle (keyboard reachable, focus มองเห็น, contrast พอ)
- โค้ดพิสูจน์ว่าเขียวด้วย `.ai/bin/gate-task.sh` ตอน flip task เป็น `[x]`: gate อ่าน
  `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD` (auto-detect script ใน package.json ให้ project แบบ Node)
  เพื่อรัน typecheck/test; เมื่อไม่มีทั้งคู่จะข้าม code-green แล้วเหลือเพียง Evidence gate

## Anti-Patterns

- ห้าม duplicate magic constant / ค่าดิบซ้ำหลายที่ (ใช้ single source แทน)
- ห้าม inline data ก้อนใหญ่ในไฟล์ presentation
- ห้ามฝังสูตรคำนวณ/business logic ตรงในตัว view
- ห้าม mark task `[x]` ทั้งที่ typecheck/test ยังไม่เขียว หรือไม่มี Evidence
- test ต้อง assert พฤติกรรมที่สังเกตได้ ไม่ใช่ snapshot รายละเอียดภายในที่เปราะ
