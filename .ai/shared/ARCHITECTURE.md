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

> สถาปัตยกรรมจริงของผลิตภัณฑ์ (ยังไม่มี source code — นี่คือ target shape).
> รายละเอียดเต็ม: `docs/reference/payment-orchestration-modules.md` · product canon: [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md)

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
- `PaymentPaid.Amount` ปัจจุบันเป็น `long` สตางค์ แต่ Orders ใช้ `decimal` บาท → ควรย้าย `Money` ไป Contracts/SharedKernel ให้ใช้ร่วม
- Orders รับ `PaymentPaid` ต้อง **verify amount/currency** ไม่ใช่แค่ `PaymentId` (กันจ่ายไม่ครบ/สกุลผิด)
- Orders ถือ `PaymentId` ตั้งแต่เรียก Payments → จับคู่ได้ทันที ไม่มี attach-race

**Cross-cutting (บังคับทั้งระบบ — security detail: [SECURITY_RULES.md](SECURITY_RULES.md) Product security):**
- Multi-tenant isolation — **floor = SQL Server native RLS + `SESSION_CONTEXT('TenantId')`** ต่อ request (ไม่พึ่ง app code);
  EF global query filter = ชั้นสะดวกเสริมไม่ใช่ floor. ban raw SQL / `IgnoreQueryFilters` ข้าม tenant + test leak. backend ร่วมกัน
- แยก backend authz scope ให้ขาด — endpoint admin (cross-tenant/approve/config) เรียกผ่าน session ของ Tenant Console ไม่ได้;
  admin cross-tenant bypass RLS ผ่าน **DB principal แยก** เท่านั้น + reason/correlation id → audit
- **Scoped-admin isolation = app-layer exception จาก RLS floor (admin-actor-rename REQ-7.4):** `pol_admin` อยู่ใน
  `pol_rls_bypass` จึงไม่ถูก RLS scope → scoped-admin cross-tenant business read ถูกบังคับผ่าน seam เดียว `IAdminQuery`
  (ฝัง `WHERE TenantId ∈ accessible`; Super = unrestricted) + Architecture.Tests ห้าม handler อื่นส่ง cross-tenant
  query ตรง + leak/bypass test = compensating control แทน RLS floor
- Credential vault — **envelope encryption (per-tenant KEK ใน KMS/HSM, DEK ต่อ secret)**, key id+version + rotation runbook; secret write-only, อ่านกลับ mask เสมอ
- Identity — Google SSO ยังทำที่ชั้น auth (verify sig/`iss`/`aud`/exp/`email_verified`; แยก console ด้วย `aud` (OAuth client ต่อ console) + `hd` guard เพราะ `iss` ร่วมกัน). **Identity module (producer-side actor) ถูกลบ 2026-06-23** + ตาราง `TenantUsers`/`ExternalLogins`/`RegistrationTickets`/`RegistrationAudits`/`TenantUserProfiles` drop แล้ว (migration `DropIdentityTables`) → จะ rebuild เป็น **Producer module** ภายหลัง. คงเหลือ `AdminAccount`* = control-plane (ไม่มี RLS predicate, pol_admin only) ใน **Admin module** ใน schema เดียว `producer` — control plane แยกขาดจาก data plane
- Maker-checker (approve tenant, เปลี่ยน routing, แก้ allowlist) · idempotency (multi-key + outbox) · audit log (append-only + tamper-evident)
- Provisioning = **saga** (DB กับ vault คนละ store, ไม่มี distributed tx): `PendingProvisioning` → write DB → write vault (idempotency key) → verify → activate ขั้นสุดท้าย → compensation/retry. validate (allowlist+schema) ก่อนเขียน + idempotent ด้วย tenant key
- Money — `Money { MinorUnits: long, Currency: ISO4217 }` ใน SharedKernel (minor-unit ตาม registry); ไม่มี decimal/float ที่ cross-module seam; Orders verify amount+currency ตอนรับ `PaymentPaid`

## Naming Conventions

- type/interface: PascalCase
- ไฟล์ logic/data: ตาม convention ของภาษา/stack ที่ project เลือก แต่คงเส้นคงวาทั้ง repo
- ค่าคงที่ที่ export: ตั้งชื่อสื่อความหมาย + มี type ชัด

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
