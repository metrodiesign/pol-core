# Plan: pol-core product canon — foundational decisions (PR #2)

_Round 3 — APPROVED by Codex (3 rounds). Final Medium nits folded in._

## Goal

ยืนยันว่าชุดการตัดสินใจ foundational ที่เพิ่งเทลง `.ai/shared/*` (product canon ของ
Internal Payment Orchestration Platform — captive, redirect-only, ไม่ถือเงิน, multi-tenant)
หนักแน่นพอให้ทั้งทีม build บนมันได้ โดยไม่มีรูที่จะแพงตอน implement (payments / schema /
multi-tenant security). canon นี้ยังไม่มี code — เป็น target shape + hard constraints ที่ทุก spec/PR
ข้างหน้าต้องเคารพ. Source จริง: `docs/reference/payment-orchestration-modules.md`.

## Approach

1. **Product model** (PROJECT_CONTEXT.md): captive (allowlist 3 นิติบุคคล), redirect-only (PCI SAQ A),
   ไม่ถือเงิน (เงิน settle PSP→merchant โดยตรง), 5 โมดูล (Products→Cart→Checkout→Orders→Payments)
   คุยผ่าน Mediator, จบที่ emit `PaymentPaid` ไม่มี issuance. 7 Non-Goals = ฟังก์ชันห้าม implement.
2. **Architecture** (ARCHITECTURE.md): Modular Monolith ตาม Clean Architecture + CQRS, 1 deployable backend,
   แยก control plane (2 console: Tenant public / Admin internal คนละ deploy บน backend ชุดเดียว) จาก
   data plane. โมดูลคุยกันผ่าน Contracts + Mediator (`INotification`) เท่านั้น.
3. **Stack pin** (CODING_STANDARDS.md): .NET 10 / ASP.NET Core 10 / C# 14 (LTS), EF Core 10,
   SQL Server 2025 Standard, martinothamar/Mediator 3.x (source-gen, compile-time). 2 schema: `admin`, `producer`.
4. **Stack profile** (stack/dotnet.md): gate `dotnet build -warnaserror` / `dotnet test`; project layout
   (SharedKernel, Contracts, Modules/<M>/{Domain,Application,Infrastructure}, Hosts/{TenantConsole,AdminConsole}).
5. **Security guardrails** (SECURITY_RULES.md product section): PCI SAQ A redirect-only, vault (encrypt+แยก key
   ต่อ tenant, write-only, mask), webhook=source of truth (verify+idempotent+fetch-to-confirm), multi-tenant RLS,
   แยก authz Admin↔Tenant, maker-checker, idempotency, audit log. กำกับว่าเป็น design-level (ไม่มี script จับ).

## Key decisions & tradeoffs (the contestable choices) — revised

1. **Modular Monolith (ไม่ใช่ microservices).** 1 deployable, backend ร่วมสำหรับ 2 console.
   Trade: blast radius กันด้วย authz scope + RLS ใน-process ไม่ใช่ network boundary. **module isolation บังคับด้วย
   architecture test** (เช่น NetArchTest: โมดูลห้าม reference `*.Domain`/`*.Infrastructure` ของโมดูลอื่น — fail build).
2. **`Money` ใน SharedKernel + wire contract ตายตัว.** seam ปัจจุบัน `PaymentPaid.Amount`=`long` สตางค์ vs Orders `decimal` บาท.
   ตัดสิน: `Money { MinorUnits: long, Currency: ISO4217 }` — **minor-unit ตาม ISO4217 registry** (THB=2), currency
   **allowlist ต่อ tenant/PSP**, overflow bounds (long, ปฏิเสธค่าติดลบ/เกิน). ไม่มี float/decimal ที่ seam. version `PaymentPaid`
   schema (`v1`). ทุกโมดูลใช้ร่วมจาก SharedKernel. Orders verify **amount + currency** ตอนรับ `PaymentPaid` ไม่ใช่แค่ `PaymentId`.
3. **RLS defense-in-depth (data-layer floor).** ชั้นจริง = **SQL Server native RLS + `SESSION_CONTEXT('TenantId')`**
   set ต่อ request (canon: "ไม่พึ่ง app code"). EF global query filter = ชั้นสะดวกเสริม **ไม่ใช่** floor. ban raw SQL /
   `IgnoreQueryFilters` ที่ข้าม tenant scope + มี test พิสูจน์ leak ปิด.
   **Admin cross-tenant bypass = path แยกชัด:** ใช้ **DB principal คนละตัว** (admin connection string) ที่ RLS predicate
   ยอมให้ข้าม — **tenant console principal ทำไม่ได้เด็ดขาด** (RLS policy ผูกกับ principal/role). ทุก bypass ต้องมี reason + correlation id → audit.
4. **Webhook = source of truth, return handler = UX เท่านั้น.** ไม่ตัดสินสถานะจาก browser redirect.
   **ห้าม trust tenant/PSP จาก URL path ก่อน verify signature** — route by connection id หรือ signed path → verify webhook secret → fetch-to-confirm.
5. **Identity: Google SSO.** verify **sig + `iss` + `aud` + `exp` + `email_verified`** เสมอ (iss ตรวจ — ไม่ skip).
   ใช้ `aud` (OAuth client ต่อ console) + `hd` guard เพื่อ **แยก console** (เพราะ `iss` ร่วมกัน = `accounts.google.com`)
   ไม่ใช่แทน iss. role ตัดสินที่ platform: lookup ตาราง identity ของ console (`AdminUser`/`TenantUser`) = allowlist จริง,
   `hd` = coarse guard เสริม. ไม่พบ/disabled → 403.
6. **Stack pin LTS.** .NET 10 + ASP.NET Core 10 + C# 14 + EF Core 10 + SQL Server 2025 Standard + Mediator 3.x.
   exact patch pin ที่ `Directory.Packages.props` ตอน implement. **เงื่อนไข: compatibility spike ก่อน canon freeze**
   (EF Core 10 provider + SQL Server 2025 GA + CI image + hosting) + fallback policy (เช่น .NET 8 LTS / SQL 2022) ถ้า spike ล้ม.
7. **Mediator lifetime.** `IMediator` Singleton (perf) ได้ แต่ **handler/pipeline ที่พึ่ง `DbContext` ต้อง Scoped**
   (หรือ inject `IDbContextFactory`) — กัน captive dependency. มี DI validation test (`ValidateScopes=true`).
8. **Provisioning = saga (ไม่ใช่ single distributed tx).** DB กับ vault คนละ store → atomic transaction เดียวเป็นไปไม่ได้.
   ลำดับ: `PendingProvisioning` → write DB → write vault (idempotency key) → verify secrets → **activate ขั้นสุดท้าย** →
   compensation/retry ถ้าล้มกลางทาง. idempotent ด้วย tenant key.
9. **Idempotency (payment/webhook).** unique key DB หลายชั้น: `(psp, eventId)` **และ** `(psp, externalChargeId, normalizedStatus)`
   (กัน PSP ที่ replay ด้วย event id ต่างกันสำหรับ charge เดียว / event ที่ไม่มี stable id) + guard ที่ **fetch-confirmed
   payment transition** `(paymentId, transition)`. atomic upsert ใน tx. `idempotencyTtlHours` = cleanup หลัง replay window
   **ไม่ใช่** guard หลัก. แยก **session expiry** (redirect) ออกจาก **event-ledger retention** (ยาวพอ PSP retry/dispute/audit).
10. **Outbox dispatcher.** publish `PaymentPaid` ผ่าน **table outbox** (เขียนในธุรกรรมเดียวกับ state transition) +
    background dispatcher แบบ **polling ด้วย lock/lease** (กัน 2 dispatcher ชนกัน), retry + **poison/DLQ** หลัง N ครั้ง,
    **idempotent consumer** (Orders dedup ด้วย event id). exactly-once *effect* ไม่ใช่ exactly-once delivery.
11. **Routing fallback (primary→fallback PSP).** fallback อนุญาต **เฉพาะก่อน PSP session ถูกสร้าง** เท่านั้น —
    หลังสร้าง charge แล้วห้าม retry ข้าม PSP (กัน duplicate charge). persist PSP external id, block second create.
12. **Config typing.** routing/session/secrets-ref = **typed table/owned types** (validation/migration/audit diff ได้) —
    `Metadata` JSON เฉพาะ low-risk display (branding/logo). secret → `VaultSecret` แยก, write-only, mask.
13. **Audit log** append-only + **tamper-evident** (immutable table policy / hash-chain / WORM export) + actor correlation id.
14. **Vault custody model (provider TBD).** envelope encryption: per-tenant **KEK** ใน **KMS/HSM** (provider เลือกตอนรู้ hosting —
    Azure Key Vault / AWS KMS / SQL Always Encrypted), DEK ต่อ secret. เก็บ **key id + version**, มี **rotation + re-encrypt runbook**,
    masked read, no log, ทุกการเข้าถึง audit. provider selection = pre-implementation task; model นี้ fix.
    **SQL Always Encrypted เข้าเกณฑ์เฉพาะเมื่อ CMK อยู่ใน external Key Vault/HSM** (แยกจาก config DB) — ลำพังไม่นับ KMS/HSM.
15. **PaymentSessionId correlation (no attach-race).** สร้าง internal `PaymentSessionId` ตอน **Orders เรียก Payments / ออก summary link**
    (ก่อนแตะ PSP) → bind `OrderId` + amount + currency + method + tenant ตั้งแต่ตอนนั้น. PSP external id ผูกทีหลังตอน confirm.
    → `PaymentPaid` จับคู่ได้ทันที ไม่มี attach-race.
16. **Security product rules** design-level + **spec-lint gate ที่ concrete (CI-enforced)**: regex/checklist fail บน —
    card input field / `Omise.js` / hosted-fields / iframe จ่าย / display-QR (non-redirect) · response ที่มี secret field ไม่ mask ·
    query/handler ที่ไม่มี tenant scope · term ของ 7 Non-Goals (settlement/payout/ledger/wallet/billing). hook เข้า `.ai/bin` + spec-trace.
    **allowlist docs/fixtures** (กัน false-pos เช่น `ledger` ใน warning prose, `iframe` ใน banned-example) + **human security checklist ควบ machine lint** (ไม่พึ่ง regex ล้วน).

## Risks / open questions (เหลือหลัง revise)

เหลือเป็น **pre-implementation task ที่ owned-by-human** (ตัดสินแล้วว่าจะทำ แต่รอ input ภายนอก) — ไม่ใช่ flaw ของ plan:

- **[GATE] Compatibility spike — task #1 ก่อน scaffold:** พิสูจน์ EF Core 10 + SQL Server 2025 GA + provider + CI image + hosting. **acceptance ชัด: pass compatibility matrix _หรือ_ record fallback decision (.NET 8 LTS / SQL 2022) → update canon ให้ตรง → ค่อยเริ่ม task #2.** tasks.md ต้อง stop หลัง spike ก่อน scaffold. **"approve plan" ≠ "freeze stack"**. risk owned by human.
- **Vault provider selection:** Azure Key Vault / AWS KMS / SQL Always Encrypted — รอ hosting decision (model envelope/per-tenant KEK fix แล้ว, decision #14).
- **Native RLS เชิง runtime:** `SESSION_CONTEXT` reset ตอน connection-pool reuse + benchmark — **acceptance: test พิสูจน์ reused pooled connection retain prior tenant context ไม่ได้** (verify ตอน infra task).
- **Notification queue tech** (Orders spec แยก) — แต่ min-contract fix now: at-least-once, idempotent notify key, DLQ alert, link token rotation/TTL.

## Canon changes required on approval (deliverable — apply หลัง human gate)

PLAN นี้ revise ตัวตัดสินใจ → ตอน approve ต้อง sync canon docs ให้ source เดียว (ตอนนี้ยังเขียนของเดิม = inconsistency ที่ track ไว้):

- `ARCHITECTURE.md` + `docs/reference/...:311` + `stack/dotnet.md`: "provisioning transaction เดียว" → **saga state machine** (decision #8)
- `SECURITY_RULES.md` + `stack/dotnet.md`: RLS = EF global filter → **native RLS + `SESSION_CONTEXT` เป็น floor**, EF filter = convenience (decision #3) + admin bypass principal (#3) + webhook ห้าม trust path ก่อน verify (#4)
- `CODING_STANDARDS.md` / `stack/dotnet.md`: Mediator lifetime — handler ที่พึ่ง DbContext = Scoped + DI validation test (#7); `Money` wire contract + ISO4217 registry ใน SharedKernel (#2)
- เพิ่ม **spec-lint gate** เข้า `.ai/bin` + CI (decision #16); audit tamper-evident (#13); idempotency multi-key + outbox (#9, #10)

> หมายเหตุ: ไม่แก้ canon ระหว่าง review loop (โดยตั้งใจ — file edit หลัง human gate). รายการนี้คือ scope ของการ apply.

## Out of scope

- เขียน code / scaffold solution จริง (repo เป็น spec-first, ยังไม่มี src/)
- 7 Non-Goals (settlement/billing/public-onboarding/card-data/PSP-functions/non-redirect/money-moving recon) — ห้ามแตะ
- spec ราย-feature (requirements/design/tasks) ของแต่ละโมดูล — ทำผ่าน /spec-* ทีหลัง
- เลือก message queue / notification worker tech (อยู่ใน Orders spec แยก)
