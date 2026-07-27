# Requirements: Captive Intra-Group Payment Alignment

> Status: approved-for-implementation 2026-07-26 (delegated autonomous run — PENDING HUMAN REVIEW
> 2026-07-27; reviewer may reject any REQ. See "Gate note".)

## Overview

canon ของแพลตฟอร์มนี้คือ **captive intra-group payment**: บริษัทในเครือ (vPrivilege / vCommerce /
vSouvenir) รับชำระเงินจากลูกค้า **ผ่าน PSP ที่ถือใบอนุญาตอยู่แล้ว** (2C2P + Omise/Opn) แบบ redirect-only
โดยเงินจริง **ไม่วิ่งผ่านแพลตฟอร์ม** — "เราใช้ PSP ไม่ใช่เป็น PSP"
(`.ai/shared/PROJECT_CONTEXT.md` §Purpose/§Business Objectives, `.ai/shared/SECURITY_RULES.md`
§Product security).

spec นี้ **ไม่เพิ่ม feature ใหม่** — ปิดช่องที่ **as-built ไม่ตรง canon นั้น**. audit โค้ดจริง
(2026-07-26, `develop` @ `ab5d6dd`) + adversarial review รอบที่สอง พบ 8 จุด:

| # | as-built ที่ยืนยันแล้ว | ขัด canon ข้อไหน | ผลจริง |
|---|---|---|---|
| A | `POST /api/v1/payments/sessions` รับ `Amount` จาก request body (`Program.cs:793-802`); `CreateSessionHandler` ไม่อ่าน `Order` เลย | "จ่ายไม่ผิด/ไม่ซ้ำ: verify amount/currency" + แพลตฟอร์มเป็น channel ห้าม mint charge เอง | client กำหนดยอดที่ส่งไป PSP ได้เอง (adapter ใช้ `session.Amount` ตรง ๆ). ยอดไม่ตรง order -> `Order.MarkPaid` throw -> `OutboxDispatcher` retry ถึง `MaxAttempts=8` แล้วค้างเป็น poison row (**ระบบนี้ไม่มีตาราง DLQ**) = ลูกค้าจ่ายเงินแล้วแต่ order ไม่ถูก fulfil |
| B | 1 order เปิดได้หลาย session พร้อมกัน ทุกใบ chargeable | "จ่ายไม่ผิด/ไม่ซ้ำ" | double-charge ลูกค้าจริงต่อ order เดียว |
| C | `Connection.Supports(method)` + `Connection.IsEnabled` **ไม่มี call site ใน flow การจ่าย** (ยืนยันซ้ำที่ `payment-orchestration-modules.md:456-459`) | eligibility ต่อ merchant ตาม `enabledMethods` | บริษัทในเครือ charge ผ่าน PSP/ช่องทางที่ตัวเองไม่ได้เปิด หรือผ่าน connection ที่ปิดอยู่ได้ |
| D | `backendReturnUrl` ที่ส่งให้ 2C2P มาจาก config **global ต่อ deployment** (`TwoCTwoPAdapter.cs:49`) แต่ route จริงคือ `/api/v1/webhooks/{pspConnectionId:guid}` | "Webhook = source of truth" + isolation ต่อบริษัท | URL global ถูกได้มากสุด **1 connection ต่อ deployment**; connection/merchant ที่เหลือ webhook ไม่ถึง handler -> order ค้าง `AwaitingPayment` + isolation แตก. (ค่าที่ CI ใช้วันนี้ `ci.yml:124` = `https://ci.example.com/webhooks/2c2p` ผิด route ซ้ำ — ไม่มี `/api/v1` และไม่ใช่ guid) |
| E | `TwoCTwoPAdapter.cs:47` hardcode `paymentChannel = ["CC"]` ไม่ดู `session.Method`; `OmiseAdapter.cs:44-50` throw `NotSupportedException` (-> 500) สำหรับ promptpay/installment | "redirect-only ครบ 3 ช่องทาง, normalize เป็นสัญญาเดียว" + adapter capability matrix (`platform-modules.md:976`) | `seed-demo.sql:102-105,387` เปิด `promptpay`/`installment` บน 2C2P และสร้าง session ด้วย method เหล่านั้นจริง -> ลูกค้าถูกส่งไปจ่ายด้วย **บัตร** เงียบ ๆ. ปิดแค่ C จะให้ความมั่นใจปลอม (ผ่าน `EnabledMethods` แล้วยังเข้าช่องผิด) |
| F | `Session.MarkFailed` / `MarkExpired` **ไม่มี production caller เลย** (call site เดียวคือ `tests/Payments.Tests/PaymentSessionTests.cs:164`); `StartRedirectHandler.cs:60-63` claim `BeginRedirect()` + save **ก่อน** resolve connection (`:76`) แล้ว throw ทีหลังโดยไม่แตะ state | state machine ต้องเดินได้จริง | session ค้าง `Redirected` + `RedirectUrl == null` ตลอดกาล -> เรียกซ้ำไม่เข้า idempotent branch (`:51`) -> 409 ถาวร. **ถ้าใส่ unique index ของ B โดยไม่แก้ข้อนี้ order นั้นจะจ่ายไม่ได้ตลอดกาล** |
| G | `IPspAdapter.FetchChargeAsync` คืนแค่ `PspChargeStatus` (`PspContracts.cs:8-13`) — ยอดที่ PSP เก็บจริงไม่เคยถูกอ่านกลับ | "จ่ายไม่ผิด: verify amount/currency ตอน Orders รับ `PaymentPaid`" | หลังปิด A แล้ว `session.Amount == order.Amount` โดยโครงสร้าง ทำให้การเทียบใน `Order.MarkPaid` (`Order.cs:155`) กลายเป็น tautology — ไม่มีชั้นไหนเทียบกับ **ยอดที่ PSP เก็บจริง** เลย |
| H | `ProvisionMerchantHandler.cs:63` แค่ `Trim()` ค่า `EnabledMethods` ที่ admin ส่งมา; `Connection.Supports` เทียบ `StringComparison.Ordinal` | vocabulary เดียวทั้งระบบ | connection ที่ถูก provision เป็น `"Card"`/`"CC"` จะทำให้ **ทุก** payment ของ merchant นั้นถูกปฏิเสธหลังปิด C (regression ที่ test ชุดใหม่มองไม่เห็นเพราะสร้าง connection เอง) |

### Gate note (ทำไม status ไม่ใช่ draft และไม่ใช่ approved เต็มใบ)

user มอบหมายงานนี้แบบ autonomous ("ไม่ต้องถามฉัน AFK") และจะตรวจละเอียดวันถัดไป. artifact ทั้ง 3
ผ่าน **fresh-context adversarial review** ด้วย `spec-architect` แทน human gate ตามที่
`.ai/shared/REVIEW_PROTOCOL.md` อนุญาตให้ subagent audit — review รอบแรกให้ verdict `REVISE` และพบ
blocker 4 ข้อ (F, E, H, และ EF/config traps) ซึ่งถูก verify กับโค้ดจริงทีละข้อแล้วยกมาเป็น REQ ในเวอร์ชันนี้.
**subagent review ไม่ใช่ human gate** — reviewer มีสิทธิ์ปฏิเสธ REQ ใดก็ได้ในวันถัดไป.

## Non-Goals — อยู่นอก scope โดยตั้งใจ (พิจารณาแล้ว ไม่ใช่ลืม)

1. **ไม่ทำ Omise webhook signature verification ในสเปกนี้** — Omise/Opn **มี** ลายเซ็น webhook
   (`Omise-Signature`, HMAC-SHA256 ด้วย webhook signing secret แยกจาก API key, รองรับหลายลายเซ็นช่วง
   rotation) และ envelope ในโค้ดก็เตรียมช่องไว้แล้ว (`PspSecretEnvelope.cs:19-25`
   `OmiseSecret.WebhookSecret`). เหตุผลที่ยัง **ไม่** ทำที่นี่คือ **seam ไม่พาข้อมูลที่ต้องใช้**:
   endpoint อ่านเฉพาะ header `X-Signature` (`Program.cs:566`) และ `VerifyWebhook(rawPayload,
   signature, secret)` ไม่มีช่อง timestamp/หลายลายเซ็น (`OmiseAdapter.cs:22-26`) — การทำให้ fail-closed
   ด้วย scheme ที่ยัง **ไม่ได้ verify กับ sandbox จริง** = หยุดการยืนยันการจ่ายทั้งหมดของ Omise.
   compensating control ที่ยังเป็น authority จริง = **fetch-to-confirm ฝั่ง server ด้วย secret ของ
   merchant เอง** (`HandlePspWebhookHandler.cs:82`) + rate limiter. **gap นี้ยังเปิด** และ REQ-5.3
   บังคับให้บันทึกไว้อย่างนั้น พร้อม next step = สเปกแยกที่ขยาย seam + verify กับ sandbox.
2. **ไม่แตะ target design ภาค 8** (`Payment` + `PaymentAttempt` split, durable webhook inbox,
   versioned routing policy, circuit breaker, fallback). สเปกนี้ปรับ as-built ให้ตรง canon **เชิง
   พฤติกรรม** ไม่ทำ re-architecture.
3. **ไม่ทำ promptpay / installment ให้ใช้งานได้จริง** — REQ-6 แค่บังคับให้ระบบ **ปฏิเสธอย่างชัดเจน**
   สิ่งที่ adapter ทำไม่ได้ แทนที่จะเข้าช่องผิดเงียบ ๆ หรือ 500. การ implement 2 ช่องทางที่เหลือต้อง
   sandbox verify (Omise PromptPay ต้องผ่าน Payment Links+ hosted page เท่านั้น — direct source+charge
   คืน QR offline = ขัด SAQ A) = สเปกแยก.
4. **ไม่ทำ session expiry sweeper** — session ที่ลูกค้าทิ้งหน้า PSP ยังกู้ได้ด้วยการเรียก
   `StartRedirect` ซ้ำ (คืน URL เดิม ไม่สร้าง charge ที่สอง) ดังนั้นไม่ใช่ deadlock; background job ที่
   เรียก `MarkExpired` ตาม TTL เป็นสเปกแยก. ข้อจำกัดที่เหลืออยู่และต้องบันทึก: **เปลี่ยน method/PSP
   หลัง redirect เริ่มแล้วไม่ได้** เพราะยังไม่มี void/cancel ที่ PSP (การเพิ่ม = แตะ funds flow).
5. **ไม่ทำ return-handler endpoint ใหม่** — browser return ยังเป็น UX นอก API เหมือนเดิม.
6. **ไม่แตะ 7 Non-Goals ของ PROJECT_CONTEXT** — ไม่มี settlement/ledger/billing/self-serve onboarding/
   card data/non-redirect/policy issuance เพิ่มแม้แต่ field เดียว.
7. **ไม่ทำ frontend return URL ต่อ merchant** — Tenant Console เป็นแอปเดียวที่ 3 บริษัทใช้ร่วมกัน
   (PROJECT_CONTEXT §Key Features) global จึงถูกต้องตามโมเดล; เฉพาะ **backend/webhook** URL ที่ต้อง
   เป็นต่อ connection.

---

## REQ-1: Payment session ตั้งราคาจาก Order เท่านั้น (server-side source of truth)

**User Story:** As a group company collecting through a licensed PSP, I want the amount charged at the
PSP to be the order's own amount, so the platform can never mint a charge the order does not back.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL อ่าน amount + currency ของ payment session จากแถว `Order` ที่ระบุด้วย `OrderId`
  เท่านั้น — ห้ามรับค่ายอดเงินจาก request body ในทุกกรณี.
- 1.2 WHEN ไม่พบ order ตาม `OrderId` ภายใน merchant ที่ผูกอยู่ THEN THE SYSTEM SHALL ตอบ `404` โดยไม่
  เปิดเผยว่ามี order นั้นอยู่ที่ merchant อื่น (no existence leak).
- 1.3 IF order ที่พบมีสถานะไม่ใช่ `AwaitingPayment` THEN THE SYSTEM SHALL ปฏิเสธด้วย `409` และไม่สร้าง
  session.
- 1.4 THE SYSTEM SHALL ถอด field `Amount` ออกจาก wire contract ของ `POST /api/v1/payments/sessions`
  (breaking change ที่ตั้งใจ — pre-prod) และถอดออกจาก `CreateSessionCommand` ด้วย.
- 1.5 THE SYSTEM SHALL บังคับกฎ 1.1-1.3 ที่ **Application handler** ไม่ใช่แค่ที่ endpoint เพื่อให้ทุก
  call site ของคำสั่งนี้ผ่านการตรวจเดียวกัน.
- 1.6 THE SYSTEM SHALL มี test ที่ pin wire contract ใหม่ (request ที่ยังส่ง `amount` มาต้องไม่ทำให้ยอด
  เปลี่ยน) และ test ที่ยืนยันว่ายอดซึ่ง adapter ประกอบลง request ของ PSP เท่ากับยอดของ order.
- 1.7 WHERE Payments module ต้องอ่านข้อมูล order THE SYSTEM SHALL ใช้ port ที่ประกาศใน
  `Payments.Application` และ implement ใน `Persistence.MerchantRuntime` — ห้าม Payments อ้าง
  `Orders.Application` โดยตรง และห้ามใช้ read surface ที่เขียน reveal-audit/คืน PII
  (`GetOrderDetailCommand`).

## REQ-2: หนึ่ง order มี session ที่ chargeable ได้ครั้งละหนึ่งใบ — และต้องไม่ล็อกตาย

**User Story:** As a customer of a group company, I want to be charged at most once for one order, and
as a producer I want a failed attempt to still be retryable, so neither double-charge nor a dead order
is possible.

**Acceptance Criteria (EARS):**
- 2.1 IF order นั้นมี session ที่สถานะยังเป็น `Created` หรือ `Redirected` และ `(Method, Psp)` **ตรงกับ
  ที่ขอมา** THEN THE SYSTEM SHALL คืน id ของ session ใบนั้น (idempotent, `200`) และ **ไม่** สร้างใบใหม่.
- 2.2 IF มี session ที่ยังเปิดอยู่ตาม 2.1 แต่ `(Method, Psp)` **ต่างจากที่ขอมา** THEN THE SYSTEM SHALL
  ปฏิเสธด้วย `409` (เปลี่ยนช่องทางกลางคันไม่ได้ เพราะยังไม่มี void/cancel ที่ PSP — Non-Goal 4).
- 2.3 IF order นั้นจ่ายสำเร็จแล้ว THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session ใหม่ (บังคับผ่าน REQ-1.3
  เพราะ order จะไม่ใช่ `AwaitingPayment` — เกณฑ์นี้บังคับให้มี test ยืนยันเส้นทางนั้นจริง).
- 2.4 THE SYSTEM SHALL บังคับ invariant 2.1/2.2 ที่ระดับ database ด้วย unique filtered index บน
  `txn.PaymentSessions` (`OrderId` เมื่อ `Status IN (0, 1)`) เพื่อให้ request ที่วิ่งพร้อมกันสร้างสองใบ
  ไม่ได้ — guard ระดับ handler อย่างเดียวแพ้ race.
- 2.5 WHEN unique index ตาม 2.4 ถูกละเมิดจาก race THEN THE SYSTEM SHALL แปลงเป็น `409` ไม่ใช่ `500`.
- 2.6 THE SYSTEM SHALL พิสูจน์ index ตาม 2.4 ด้วย assertion ชั้น **offline** ด้วย (ชื่อ index, `IsUnique`,
  filter ตรงตัว, ครบทั้งสองไฟล์ `SessionConfiguration`) ไม่ใช่ integration test เพียงอย่างเดียว เพราะ CI
  ข้าม job integration เมื่อไม่มี secret (`ci.yml:128-143`).
- 2.7 WHEN migration ของ 2.4 ถูก apply กับฐานข้อมูลที่ **มี open session ซ้ำต่อ order อยู่ก่อน** (สภาพที่
  create-session เวอร์ชันก่อนหน้าอนุญาต) THEN THE SYSTEM SHALL สะสางแถวซ้ำแบบ deterministic ใน migration
  นั้นเอง ก่อนสร้าง index — เก็บใบที่มี `PspExternalChargeId` ผูกอยู่ (ใบที่ลูกค้าอาจกำลังจ่าย) เป็นอันดับแรก
  ที่เหลือเลือกใบใหม่สุด แล้วผลักใบที่ **ไม่มี** charge ผูกไปสถานะ `Expired`; IF ยังเหลือ order ที่มีใบซึ่ง
  **ผูก charge ไว้** มากกว่าหนึ่งใบ THEN THE SYSTEM SHALL หยุด migration พร้อมข้อความที่ระบุ `OrderId`
  เหล่านั้น (การ expire ใบที่มี charge จริงจะทำให้ webhook `MarkPaid` throw = poison ตลอดไป จึงต้องให้คน
  ตัดสิน) — ห้ามปล่อยให้ `CREATE UNIQUE INDEX` ล้มกลาง deployment chain โดยไม่มีทางสะสาง.

## REQ-3: บังคับ eligibility ของ PSP connection ต่อบริษัท (channel enforcement)

**User Story:** As the central team, I want each group company to charge only through the PSP and the
channels its own connection actually enables, so a misconfigured or disabled connection cannot take
money.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL ตรวจว่า `Method` ที่ขอมาอยู่ใน `Connection.EnabledMethods` ของ (merchant, psp)
  นั้น **ก่อน** เรียก PSP ทุกครั้ง.
- 3.2 IF `Connection.IsEnabled` เป็น false THEN THE SYSTEM SHALL ปฏิเสธด้วย `409` และไม่เรียก PSP
  (สถานะฝั่ง server ไม่ใช่ input ที่ผิดของ client).
- 3.3 WHEN ไม่มี connection ของ (merchant, psp) ที่ขอ THEN THE SYSTEM SHALL ปฏิเสธที่ขั้น
  create-session ด้วย `409` (ไม่ปล่อยให้ไปพังตอน redirect).
- 3.4 IF `Method` ไม่ใช่รหัส canonical (`card` / `promptpay` / `installment`) THEN THE SYSTEM SHALL
  ปฏิเสธด้วย `400` ที่ขั้น create-session แทนที่จะเป็น `500` จาก `NotSupportedException` ของ adapter.
- 3.5 WHILE session กำลังจะถูก redirect THE SYSTEM SHALL ตรวจ eligibility ซ้ำ **ก่อน** claim
  (`BeginRedirect`) และก่อน reveal secret เพราะ connection อาจถูกปิดหรือแก้ `EnabledMethods` ระหว่าง
  create กับ redirect.
- 3.6 THE SYSTEM SHALL รวม logic eligibility ไว้ที่จุดเดียวบน `Payments.Domain.Psp.Connection`
  (ห้าม duplicate เงื่อนไขในสอง handler) และ `Connection.Supports` ต้องมี call site จริงหลังงานนี้.
- 3.7 WHEN admin provision connection THE SYSTEM SHALL normalize + validate `EnabledMethods` ด้วย
  vocabulary เดียวกับ 3.4 และปฏิเสธค่าที่ไม่รู้จัก เพื่อไม่ให้ค่าอย่าง `"Card"`/`"CC"` ทำให้ทุกการจ่าย
  ของ merchant นั้นถูกปฏิเสธภายหลัง.

## REQ-4: Webhook callback URL ต่อ PSP connection (ไม่ใช่ global ต่อ deployment)

**User Story:** As a group company with my own PSP connection, I want the PSP to notify the exact
callback URL bound to my connection, so payment confirmations actually arrive and stay isolated per
company.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL ประกอบ backend-notification URL ที่ส่งให้ PSP เป็นต่อ connection ในรูป
  `{Psp:PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}` โดยใช้ connection id ของ connection ที่ถูก
  ใช้ charge จริง.
- 4.2 THE SYSTEM SHALL เลิกใช้และลบ config `Psp:TwoCTwoP:BackendReturnUrl` (รวม env
  `PSP_TWOCTWOP_BACKEND_RETURN_URL`) ออกจากทุกที่ที่อ้างถึง: `PspOptions`, `docker-compose.prod.yml`,
  `.env.prod.example`, และ env ของ job ใน `.github/workflows/ci.yml` (และ `.gitlab-ci.yml` ถ้ามี).
- 4.3 IF `Psp:PublicBaseUrl` ไม่ถูกตั้งค่า หรือไม่ใช่ absolute URI THEN THE SYSTEM SHALL fail fast
  ตอน startup **ในสภาพแวดล้อมที่ไม่ใช่ Development** พร้อมข้อความที่ระบุชื่อ config key.
- 4.4 THE SYSTEM SHALL คง browser-facing return URL (`Psp:TwoCTwoP:FrontendReturnUrl`,
  `Psp:Omise:ReturnUri`) เป็น global ตามเดิม เพราะ Tenant Console เป็นแอปเดียวที่ 3 บริษัทใช้ร่วมกัน.
- 4.5 WHERE PSP กำหนด webhook URL จาก dashboard ไม่ใช่จาก request (Omise/Opn) THE SYSTEM SHALL บันทึก
  ขั้นตอน ops ที่ต้องตั้ง URL ต่อ connection ลงใน runbook (`docs/runbooks/deploy-self-host.md`) แทน
  การพยายามส่งค่าใน request.
- 4.6 THE SYSTEM SHALL ไม่ทำให้ host ที่มีอยู่ boot ไม่ขึ้นหรือ test suite เดิมล้มจากการเพิ่ม config key
  ใหม่ — `appsettings.json` ต้องมี placeholder ของ `Psp:PublicBaseUrl` และการบังคับตาม 4.3 ต้องไม่ทำงาน
  ใน Development (ตรวจนับ: `dotnet test pol-core.slnx --filter "Category!=Integration"` ต้องเขียวเท่าเดิม).

## REQ-5: เอกสาร as-built ตรงกับโค้ดหลังงานนี้ — และไม่เคลมเกินจริง

**User Story:** As the next agent or reviewer, I want the reference docs to state exactly what the code
now does and what is still open, so nobody re-opens a closed gap or trusts an open one as closed.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL อัปเดต `docs/reference/payment-orchestration-modules.md` ทุกย่อหน้า
  `[as-built ...]` ที่สเปกนี้ทำให้ล้าสมัย (create-session ที่รับ amount จาก body, method router ที่
  "ยังไม่มีจริง"/`Connection.Supports` ที่ไม่มี call site, backend URL ที่เป็น global) ให้ตรงกับพฤติกรรม
  ใหม่ พร้อมวันที่ และห้ามใช้ emoji ในไฟล์ `.md`.
- 5.2 THE SYSTEM SHALL อัปเดตทะเบียน gap ใน `docs/reference/platform-modules.md` โดยปิดเฉพาะรายการที่
  ปิดจริง.
- 5.3 THE SYSTEM SHALL NOT บันทึกว่าปิดแล้วสำหรับ 3 เรื่องที่ยังเปิด — (ก) Omise webhook signature
  verification (Non-Goal 1, ต้องบันทึกเหตุผล seam ที่ถูกต้อง ไม่ใช่ข้ออ้างว่า Opn ไม่มีลายเซ็น),
  (ข) promptpay/installment ที่ adapter ยังทำไม่ได้ (Non-Goal 3), (ค) การเทียบยอดกับ PSP ในกรณีที่
  response ของ PSP ไม่ส่งยอดกลับมา (REQ-8.3) — แต่ละข้อต้องคงอยู่ในทะเบียนพร้อมเหตุผลและ next step.

## REQ-6: Method ต้องอยู่ในความสามารถจริงของ adapter (ห้ามเข้าช่องผิดเงียบ ๆ)

**User Story:** As a customer, I want the channel I picked to be the channel I actually pay through, so
choosing PromptPay never silently sends me to a card page.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL ให้ adapter ของแต่ละ PSP ประกาศชุด method ที่ตัวเอง honour ได้จริง (วันนี้:
  2C2P = `card`, Omise = `card`) เป็นข้อมูลที่ Application layer อ่านได้.
- 6.2 IF `Method` ที่ขอมาอยู่ใน `Connection.EnabledMethods` แต่ **ไม่อยู่ใน** ชุดตาม 6.1 THEN THE SYSTEM
  SHALL ปฏิเสธที่ขั้น create-session ด้วย `409` และไม่สร้าง session.
- 6.3 THE SYSTEM SHALL ให้ `paymentChannel` ที่ส่งไป 2C2P มาจาก `Session.Method` ผ่าน mapping ที่ระบุ
  ชัด แทนค่าคงที่ `["CC"]` ที่ hardcode อยู่.
- 6.4 THE SYSTEM SHALL NOT ส่ง method ที่ adapter honour ไม่ได้ไปถึง PSP เลย — ไม่ substitute ช่องทาง
  และไม่พึ่ง `NotSupportedException` ตอน redirect เป็นด่านหลัก.
- 6.5 THE SYSTEM SHALL ปรับ demo seed (`docker/bootstrap/seed-demo.sql`) ให้ session ที่ seed ไม่ขัดกับ
  6.2 เพื่อไม่ให้ข้อมูลตัวอย่างขัดกับกฎที่บังคับใช้จริง.

## REQ-7: Session ต้องไม่ค้างในสถานะที่เดินต่อไม่ได้ (liveness)

**User Story:** As a producer whose customer's card was declined, I want to start a fresh payment
attempt, so a failed attempt does not kill the order permanently.

**Acceptance Criteria (EARS):**
- 7.1 WHEN การสร้าง charge ล้มเหลวด้วยเหตุที่พิสูจน์ได้ว่า **ยังไม่มี request ไปถึง PSP หรือ PSP ปฏิเสธ
  อย่างเด็ดขาด** THEN THE SYSTEM SHALL เปลี่ยน session เป็น `Failed` แล้วบันทึกก่อนส่ง error ออกไป —
  ห้ามปล่อยให้ session ค้างในสถานะที่เดินต่อไม่ได้.
  (amended 2026-07-27, Codex review #4782168269 P1: ถ้อยคำเดิม "WHEN การสร้าง charge ที่ PSP ล้มเหลว"
  ครอบ **ทุก** exception ซึ่งรวม timeout/cancel/transport/parse ที่ PSP อาจรับ charge ไปแล้ว — แล้วการ
  เปิด session ใหม่จะได้ `Session.Id` ใหม่ = idempotency key ใหม่ที่ PSP = **charge ที่สอง**.)
- 7.2 THE SYSTEM SHALL NOT ปล่อยให้มี session ที่สถานะ `Redirected` แต่ `RedirectUrl` เป็น null หลังจบ
  request ใด ๆ.
- 7.3 THE SYSTEM SHALL ปฏิเสธคำขอ redirect ที่ผิด eligibility/ไม่มี connection **ก่อน** claim
  (`BeginRedirect`) เพื่อให้คำขอที่ถูกปฏิเสธไม่ทำให้ session เปลี่ยนสถานะเลย.
- 7.4 WHERE session ของ order เป็น `Failed` THE SYSTEM SHALL อนุญาตให้เปิด session ใหม่ของ order เดิมได้
  และต้องมี test ที่พิสูจน์เส้นทาง fail-then-retry ทั้งเส้น ไม่ใช่แค่ประกอบ session สถานะ `Failed` ขึ้นมา
  ในหน่วยความจำ.
- 7.5 IF ผลลัพธ์จาก PSP **กำกวม** (timeout / cancellation / transport fault / 5xx / อ่าน-หรือ-verify
  response ไม่ได้) THEN THE SYSTEM SHALL **คงสถานะ claim ของ session ใบเดิมไว้** ห้ามเปลี่ยนเป็น `Failed`
  และห้ามเปิดทางให้ order เดิมสร้าง session ใบใหม่ — เพราะ charge ที่ PSP อาจเกิดขึ้นแล้วและ session ใหม่จะ
  ได้ idempotency key ใหม่ = จ่ายซ้ำ.
- 7.6 WHERE session อยู่สถานะ `Redirected` แต่ยังไม่มี `RedirectUrl` (ผลกำกวมตาม 7.5 หรือ claim ที่ยัง
  สะสางไม่จบ) THE SYSTEM SHALL ให้คำขอ redirect ครั้งถัดไป **สะสาง claim นั้นโดยเรียก PSP ซ้ำด้วย
  idempotency key เดิมของ session** แล้วผูกผลที่ได้ — ห้ามตอบ 409 ตายตัว และห้ามสร้าง charge ใบที่สอง
  (adapter ทั้งสองตัว derive key จาก `Session.Id` อยู่แล้ว: 2C2P `invoiceNo`+`idempotencyID`, Omise
  `Idempotency-Key` — การเรียกซ้ำจึงคืน charge เดิม ไม่ใช่ใบใหม่ และทำให้ webhook correlate ได้อีกครั้ง).

## REQ-8: ยอดที่ PSP ยืนยันต้องถูกเทียบก่อนบันทึกว่าจ่ายแล้ว

**User Story:** As the central team, I want the amount the PSP actually collected to be compared with
the order's amount before the order is marked paid, so a wrong-amount collection is never fulfilled.

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL ให้ fetch-to-confirm คืนทั้งสถานะ **และยอด+สกุลเงินที่ PSP รายงาน** เมื่อ response
  ของ PSP มีข้อมูลนั้น.
- 8.2 IF ยอดที่ PSP ยืนยันมีค่าและไม่เท่ากับ `Session.Amount` (ยอดหรือสกุลเงิน) THEN THE SYSTEM SHALL
  ไม่เปลี่ยน session เป็น `Paid` และไม่ enqueue `PaymentPaid`.
- 8.3 WHERE response ของ PSP ไม่มียอดกลับมา THE SYSTEM SHALL ยืนยันด้วยสถานะเพียงอย่างเดียวตามเดิม
  (ห้าม fail-closed บน contract ที่ยังไม่ได้ verify กับ sandbox) และ gap นี้ต้องถูกบันทึกตาม REQ-5.3.
- 8.4 THE SYSTEM SHALL NOT เปลี่ยน idempotency key, ลำดับ transaction, หรือสัญญา `PaymentPaid` จากการ
  เพิ่มการตรวจนี้.
</content>
