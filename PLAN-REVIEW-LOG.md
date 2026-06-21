# Plan Review Log: pol-core product canon — foundational decisions (PR #2)

Started session start. MAX_ROUNDS=5.

## Round 1 — Codex

**Critical**
- PLAN:36 RLS เลือก EF global filter app-layer แต่ canon บอก data-layer isolation/ไม่พึ่ง app code; raw SQL, `IgnoreQueryFilters`, admin context, cross-context query รั่วได้. Fix: SQL Server native RLS + `SESSION_CONTEXT` บังคับ หรือแก้ canon + ban/test raw SQL bypass.
- PLAN:38 identity บอก "ไม่ใช่ iss" แต่ security บังคับ verify iss; token ปลอม/ผิด issuer ผ่านได้. Fix: verify sig/iss/aud/exp/email_verified; ใช้ aud+hd แยก console ไม่ใช่แทน iss.
- PLAN:48 vault ยังเปิด KMS/key custody/rotation. Fix: envelope encryption, per-tenant KEK, KMS/HSM owner, key id/version, rotation/re-encrypt, masked read, no log, audit.
- PLAN:49 idempotency race; webhook ซ้ำพร้อมกันอาจ emit PaymentPaid สองครั้ง. Fix: unique key (psp,eventId)+(paymentId,transition), atomic upsert tx, outbox publish once, TTL=cleanup หลัง replay window ไม่ใช่ guard หลัก.
- ref:311 "DB+Vault transaction เดียว" ขัด vault แยกที่ไม่มี distributed tx. Fix: provisioning saga (PendingProvisioning, write DB, write vault w/ idempotency key, verify, activate last, compensation/retry).

**High**
- PLAN:33 Money ใน SharedKernel ยังไม่กำหนด wire contract; ย้าย long/decimal เสี่ยง breaking serialization+rounding. Fix: Money{minorUnits long, currency ISO4217, scale}, version PaymentPaid, ban decimal at seam.
- ARCH:84 "Orders ถือ PaymentId ตั้งแต่เรียก Payments" แต่ flow บอก PSP session สร้างตอน confirm; attach-race ยังไม่พิสูจน์หาย. Fix: สร้าง internal PaymentSessionId ตอน issue summary link, bind OrderId/amount/currency/method ก่อน redirect.
- PLAN:52 Mediator singleton + scoped DbContext = captive dependency. Fix: handlers/pipeline scoped หรือ inject IDbContextFactory, + DI validation test.
- PLAN:51 .NET10/EF10/SQL2025 future/availability risk (provider, hosting, CI images). Fix: compatibility spike ก่อน canon freeze; pin GA policy + fallback stack.
- ref:232 routing fallback ไม่กำหนด failure semantics; retry fallback หลัง PSP create อาจ duplicate charge. Fix: fallback เฉพาะก่อน PSP session created; persist PSP external id, block second create.
- ref:237 idempotencyTtlHours:24 อาจสั้นกว่า PSP replay/dispute. Fix: แยก session expiry จาก webhook idempotency retention; event ledger ยาวพอ PSP retry/audit.
- PLAN:27 product security design-level only; ไม่มี automated gate. Fix: spec lint/checklist gate สำหรับ banned flows/card fields/non-redirect/missing tenant filter/secret response.

**Medium**
- ref:291 Tenant.Metadata JSON ทำ validation/migration/audit ยาก. Fix: typed tables/owned types สำหรับ high-risk config; JSON เฉพาะ display.
- ref:323 Admin hd=platform.com assumes all domain users admin-eligible. Fix: explicit AdminUser allowlist ทุก login; hd = coarse guard.
- PLAN:60 queue/worker out of scope แต่ notification TTL/DLQ เป็น core dependency. Fix: define min contract now (at-least-once, idempotent key, DLQ alert, token rotation).
- SEC:182 audit append-only แต่ tamper model ไม่ระบุ. Fix: immutable table/hash chain/WORM export + actor correlation id.
- ref:296 webhook URL path embeds tenant/PSP; ไม่มี secret path/signature mismatch handling. Fix: route by connection id หรือ signed path + verified webhook secret; never trust path tenant ก่อน signature/fetch confirm.

VERDICT: REVISE

### Claude's response

รับเกือบทั้งหมด (legit + ยกระดับ canon). แก้ PLAN.md:
- **รับ:** RLS defense-in-depth (native RLS+SESSION_CONTEXT floor, EF filter ชั้นสะดวก, ban raw SQL bypass+test) · vault envelope+per-tenant KEK+KMS+rotation · idempotency unique-key+atomic upsert+outbox, TTL=cleanup · provisioning saga (ไม่ใช่ distributed tx) · Money{minorUnits:long,currency:ISO4217} ban decimal at seam · Mediator handler scoped/IDbContextFactory+DI validation test · routing fallback เฉพาะก่อน PSP session created + persist external id · webhook ห้าม trust path ก่อน verify signature · typed config (JSON เฉพาะ display) · audit hash-chain/WORM · queue min-contract · แยก session-expiry จาก webhook-idempotency retention · spec-lint checklist gate (PCI/non-goals)
- **แก้ wording (ไม่ใช่ flaw จริง):** iss — canon verify sig/iss/aud/exp/email_verified อยู่แล้ว (SECURITY enforcement line); aud+hd = แยก console ไม่ใช่แทน iss. PLAN เดิมเขียนกำกวม → ชัดขึ้น.
- **รับบางส่วน:** version — ไม่เปลี่ยน pin (.NET10/SQL2025 user ตัดสิน) แต่เพิ่ม compatibility-spike + fallback-policy เป็น risk ที่ต้องเคลียร์ก่อน freeze. attach-race: canon ถูกแล้ว (internal PaymentId เกิดตอน Orders เรียก Payments ก่อนแตะ PSP) → ระบุ PaymentSessionId ชัด. admin hd: table lookup = allowlist อยู่แล้ว → clarify.

## Round 2 — Codex

**Critical** (ส่วนใหญ่ = PLAN เปลี่ยนแต่ canon docs ยังของเดิม → inconsistency)
- PLAN:37 RLS แก้แล้ว แต่ SECURITY_RULES:174 + stack/dotnet.md:55 ยังพูด EF/global filter เป็น RLS floor. Fix: update canon docs ให้ source เดียว.
- PLAN:51 provisioning saga ดีขึ้น แต่ ARCH:92 + dotnet.md:56 + ref:311 ยังสั่ง "transaction เดียว". Fix: replace single-tx language ด้วย saga ทุกที่.
- PLAN:67 compatibility spike ยัง "บล็อก canon freeze"; approve ไม่ได้ตอน stack viability ยังไม่เคลียร์. Fix: รัน spike ก่อน หรือ downgrade เป็น fallback stack.
- PLAN:69 vault custody ยังเปิด. Fix: เลือก KMS/HSM provider/ownership, envelope, key versioning, rotation runbook ก่อน implement.
- PLAN:72 PaymentSessionId ยังเป็น open question. Fix: promote เป็น key decision.

**High**
- PLAN:39 "admin bypass RLS by intent" underspecified. Fix: separate admin DB principal/procedure, reason/correlation id, block tenant principal จาก bypass.
- PLAN:54 (psp,eventId) อาจไม่ stable / PSP replay event id ต่าง. Fix: unique เพิ่ม (psp,externalChargeId,normalizedStatus) + fetch-confirmed transition.
- PLAN:55 outbox dispatcher semantics เปิด. Fix: table outbox + polling lock/lease, retry, poison queue, idempotent consumer.
- PLAN:62 spec-lint ยังไม่ concrete. Fix: rules/regex/checklist + CI/spec-trace fail บน banned term/secret response/card UI.
- PLAN:34 Money ขาด minor-unit table + currency validation. Fix: ISO4217 minor-unit registry, currency allowlist ต่อ tenant/PSP, overflow bounds, JSON version.

**Closed From Round 1:** identity iss/sig/aud/exp ✓ · PSP fallback blocked after create ✓ · Mediator scoped/DI validation ✓ · config typed routing/session ✓

VERDICT: REVISE

### Claude's response

- **ปิด canon-inconsistency criticals:** เพิ่ม section "Canon changes required on approval" ใน PLAN ระบุ edit ชัด (RLS, saga, Money, lifetime) — deliverable ที่ track. ไม่แก้ canon ตอนนี้ = loop rule (file edit หลัง human gate); ระบุชัดเพื่อไม่ให้เป็น contradiction ซ่อน.
- **Promote เป็น key decision:** PaymentSessionId · vault custody model · outbox dispatcher · admin-bypass path · Money minor-unit registry · spec-lint concrete rules.
- **รับ High ทั้งหมด:** idempotency เพิ่ม (psp,externalChargeId,normalizedStatus) · outbox lease/poison/idempotent consumer · spec-lint regex+CI fail · Money ISO4217 registry+allowlist+overflow · admin separate DB principal.
- **ยืนตำแหน่ง (logged disagreement ถ้า Codex คง REVISE):** compatibility spike — ไม่ downgrade pin (.NET10/SQL2025 = user decision). plan approve ได้โดยมี spike เป็น **first implementation task (gate ก่อน scaffold)** + fallback named + risk owned by human. "approve plan" ≠ "freeze stack ทันที". vault provider = pre-implementation task (รอ hosting decision) แต่ model fix แล้ว.

## Round 3 — Codex

**Critical/High:** ไม่พบ blocker ใหม่. ปิดประเด็นเดิม: RLS native + admin principal แยก, saga แทน distributed tx, idempotency multi-key + outbox, PaymentSessionId ก่อน PSP, canon sync tracked เป็น deliverable.

**Medium (nits)**
- PLAN:69 SQL Always Encrypted อาจไม่แยก KMS/HSM จาก config DB. Fix: จำกัด wording = "Always Encrypted only with external CMK in Key Vault/HSM; otherwise not eligible".
- PLAN:75 spec-lint regex อาจ false-pos/neg (`ledger` ใน warning, `iframe` ใน banned-example). Fix: allowlisted docs/fixtures + human security checklist ควบ machine lint.
- PLAN:83 fallback owned-by-human ดี แต่ tasks.md ต้อง stop หลัง spike ก่อน scaffold. Fix: task#1 acceptance explicit (pass matrix หรือ record fallback) → update canon → ค่อย task#2.
- PLAN:85 RLS pooling reset ต้องมี concrete acceptance. Fix: test พิสูจน์ reused pooled connection retain prior tenant context ไม่ได้.

**Residual Risk:** เหลือ pre-implementation gate ไม่ใช่ plan flaw (stack spike, vault provider, RLS runtime, queue tech). "Plan sound ถ้า approval = approve decision path + required canon edits, ไม่ใช่ freeze stack without spike."

VERDICT: APPROVED

### Claude's response
Converged รอบ 3. Fold 4 Medium nit เข้า PLAN (cheap + ถูก): Always Encrypted CMK wording · spec-lint allowlist fixtures + human checklist · task#1 acceptance explicit · RLS pooling test acceptance. ไม่มี disagreement ค้าง.
