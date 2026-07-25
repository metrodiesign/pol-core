# Requirements: Insurance Policy-Reference Record

> Status: approved 2026-07-22, amended 2026-07-23

## Overview

Platform นี้ขาย/นำเสนอประกันภัยผ่าน PSP (2C2P/Omise) — **เป็นตัวกลาง ไม่ใช่บริษัทประกัน ไม่ออกกรมธรรม์เอง**
(`.ai/shared/PROJECT_CONTEXT.md`). วันนี้ flow จบที่ `Order.Paid` และเก็บผู้เอาประกัน 1 คนต่อ `OrderLine`
(insurance-pivot). Feature นี้เพิ่มความสามารถ **บันทึกข้อมูลอ้างอิงกรมธรรม์ที่ออกโดยบริษัทประกันภายนอก**
(เลขกรมธรรม์/รับแจ้ง/สลักหลัง/ต่ออายุ, ประเภทประกัน, ทะเบียนรถ, เบี้ยสุทธิ/รวม, สถานะตัดชำระเบี้ย) ผูกกับ line
ที่ขายไปแล้ว แล้วแสดงเป็น **ตารางรายงาน policy รายผู้เอาประกัน** ให้ Admin และ Merchant/Producer ดู
(ตาม UI ที่ทีมส่งมา). ข้อมูลทั้งหมด **มาจากระบบภายนอก** — spec นี้รับด้วยการ **กรอก/แก้ทีละ record**;
bulk/automated import จากระบบภายนอกเป็น spec แยกต่างหาก.

## ADR-1: Store externally-issued policy references (supersedes insurance-pivot exclusions)

**Status:** Accepted 2026-07-22 — supersedes `insurance-pivot` **REQ-3.4** และ **Non-Goals** ที่เกี่ยวข้อง.

**สิ่งที่ถูก supersede:**
- insurance-pivot REQ-3.4 (`requirements.md:128`): "SHALL NOT เพิ่ม state, entity, field หรือ event ใดๆ
  ที่แสดงถึงการออกกรมธรรม์ (เลขกรมธรรม์, policy document/PDF, issuance workflow)".
- insurance-pivot Non-Goals (`:227,:230`): "ห้ามเพิ่ม claims / renewal / endorsement / ..." และ
  "ห้ามสร้าง entity ใหม่ (`Policy`, `InsurancePlan`, `Insurer`)".

**Decision:** อนุญาตให้ **เก็บ** เลขกรมธรรม์/รับแจ้ง/สลักหลัง/ต่ออายุ + สถานะตัดชำระเบี้ย + เบี้ยสุทธิ/รวม
เป็น **external reference data** ผูกกับ line ที่ขายไปแล้ว. ถ้า design เลือกแยกเป็น entity `PolicyRecord`
ต่างหาก (แทนการเพิ่ม field บน `OrderLine`) — อนุญาตให้สร้าง entity นั้นได้ภายใต้ ADR นี้.

**Rationale — ทำไมไม่ขัด thesis "ไม่ใช่ insurer":**
การ **เก็บเลขที่บริษัทประกันภายนอกออกให้** ≠ การ **ออก** กรมธรรม์เอง. Platform ยัง:
ไม่ generate เลขกรมธรรม์เอง, ไม่ออก policy document/PDF, ไม่มี issuance/underwriting workflow,
ไม่ทำ claims/commission/reinsurance. เลขและสถานะทุกตัว **ป้อนจากภายนอก** เท่านั้น.

**ยังคงไม่แตะ (ไม่ถูก supersede):** `Order.MarkPaid` state machine + `OrderStatus` lifecycle
(insurance-pivot REQ-3.1), `Payments.Domain.Session`/`SessionStatus`/PSP adapter (REQ-3.2),
issuance/underwriting/claims/commission/reinsurance workflow ยัง **ห้าม** ทั้งหมด.

---

## REQ-1: Line carries external insurance-reference attributes

**User Story:** As a producer/admin, I want a sold line to hold the insurance-policy details issued by
the external insurer, so the platform can report the real policy each customer bought.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL ผูกชุดข้อมูล insurance-reference ต่อ **1 line ต่อ 1 ผู้เอาประกัน** (สอดคล้อง
  insurance-pivot: 1 person/line) ประกอบด้วย field ใน REQ-1.2–1.6.
- 1.2 THE SYSTEM SHALL เก็บ `InsuranceCategory` เป็น enum ค่า `Voluntary` (ภาคสมัครใจ) หรือ
  `Compulsory` (ภาคบังคับ/พ.ร.บ.).
- 1.3 THE SYSTEM SHALL เก็บ `ReferenceNumberType` เป็น enum ค่า `PolicyNumber` (เลขกรมธรรม์) หรือ
  `NotificationNumber` (เลขรับแจ้ง), และเก็บ `ReferenceNumber` เป็นสตริงค่าอ้างอิงตามชนิดนั้น.
- 1.4 THE SYSTEM SHALL เก็บ `EndorsementNumber` (สลักหลัง) และ `RenewalReminderNumber` (เลขใบเตือน
  ต่ออายุ) เป็น field แยก ที่เป็น optional (nullable) และมีค่าได้พร้อมกับ `ReferenceNumber`.
- 1.5 THE SYSTEM SHALL เก็บ `InsuredObjectReference` (ข้อมูลอ้างอิงวัตถุที่เอาประกัน เช่นทะเบียนรถ)
  เป็น optional (nullable) สำหรับทุกประเภทประกัน.
- 1.6 THE SYSTEM SHALL เก็บ `NetPremium` (เบี้ยสุทธิ) และ `GrossPremium` (เบี้ยรวม) เป็น `Money`
  (`DECIMAL(19,4)` + currency `THB`, ตาม CODING_STANDARDS) แยกจากยอดเดิม `Line.UnitPrice`; ทั้งสองเป็น
  nullable และต้องมาเป็นคู่ (ดู REQ-3.12).
- 1.7 WHERE line ยังไม่มีข้อมูล external, THE SYSTEM SHALL อนุญาตให้ทุก field ใน REQ-1.2–1.6
  เป็นค่าว่าง/unset (line ที่ขายแล้วแต่ยังไม่ป้อนกรมธรรม์ยังต้องมีอยู่ได้).
- 1.8 THE SYSTEM SHALL ทำให้ field เหล่านี้ **generic ต่อทุกประเภทประกัน** — ไม่มี field ใดที่บังคับ
  เฉพาะ Motor; ทะเบียนรถและภาคบังคับเป็นเพียงกรณีใช้งานของ field generic ข้างต้น.
- 1.9 THE SYSTEM SHALL persist ข้อมูลทั้งหมดใน REQ-1 ให้คงอยู่ข้าม process restart.
- 1.10 THE SYSTEM SHALL อนุญาต `ReferenceNumber` ค่าซ้ำกันข้ามหลาย line ได้ (ไม่มี unique constraint) —
  platform ไม่ใช่ authority ของการออกเลข การบังคับ unique จะ block ข้อมูลจริงจากภายนอก.
- 1.11 THE SYSTEM SHALL แทนค่า unset ของ enum `InsuranceCategory`/`ReferenceNumberType` ด้วย `null`
  (nullable column; `null` = ยังไม่ป้อนข้อมูล external ตาม REQ-1.7) — ไม่เพิ่ม enum member สำหรับ "unset".

## REQ-2: Premium remittance status

**User Story:** As a producer/admin, I want to record whether the premium has been remitted (deducted) to
the insurer, so the report reflects settlement status independently of the customer's payment.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL เก็บ `PremiumRemittanceStatus` เป็น enum ค่า `NotApplicable` (N/A) หรือ
  `Deducted` (ตัดชำระเบี้ยแล้ว), ค่าเริ่มต้น = `NotApplicable`.
- 2.2 WHEN ตั้ง `PremiumRemittanceStatus` = `Deducted`, THE SYSTEM SHALL บันทึก `DeductedAt` — **วันที่ตัด
  ชำระจริงจากระบบ insurer ภายนอก (client-supplied date, ไม่ใช่ server timestamp)** — ที่มากับค่านั้น.
- 2.3 IF ตั้ง `PremiumRemittanceStatus` = `Deducted` โดยไม่มี `DeductedAt`, THEN THE SYSTEM SHALL
  ปฏิเสธคำขอด้วย 400 (Bad Request).
- 2.4 WHERE `PremiumRemittanceStatus` = `NotApplicable`, THE SYSTEM SHALL ไม่บังคับให้มี `DeductedAt`.
- 2.5 IF `DeductedAt` เป็นวันในอนาคต (> วันปัจจุบัน), THEN THE SYSTEM SHALL ปฏิเสธด้วย 400 (mirror
  invariant `InsuredDateOfBirth > nowUtc` ที่ `Line.cs`).
- 2.6 WHEN `PremiumRemittanceStatus` เปลี่ยนจาก `Deducted` → `NotApplicable`, THE SYSTEM SHALL clear
  `DeductedAt` และเขียน audit record ของการเปลี่ยนแปลงนี้ (ตาม REQ-3.5 — ไม่ใช่ silent side-effect).

## REQ-3: Capture & update reference data (write path)

**User Story:** As an admin or producer, I want to enter or correct the external policy data for a line,
so the record matches the insurer's system.

**Acceptance Criteria (EARS):**
- 3.1 WHEN actor ที่ได้รับอนุญาตส่งข้อมูล insurance-reference สำหรับ line ที่มีอยู่, THE SYSTEM SHALL
  persist ค่านั้นและตอบสำเร็จ.
- 3.2 THE SYSTEM SHALL อนุญาตทั้ง **Admin** (ผ่าน Admin plane) และ **Producer/Merchant** (ผ่าน
  MerchantUser plane) ให้เขียนข้อมูลนี้ได้ ภายใต้ permission แยกของแต่ละ plane.
- 3.3 WHILE actor เป็น Producer/Merchant, THE SYSTEM SHALL อนุญาตให้เขียนได้เฉพาะ line ที่อยู่ภายใต้
  merchant ของตนเท่านั้น.
- 3.4 THE SYSTEM SHALL อนุญาตให้เขียนได้ **หลังจาก Order ถูกสร้างแล้ว** และให้ **แก้ซ้ำได้ (mutable)**
  โดยไม่ผูกกับสถานะ `Order.Paid` (ป้อนได้ทั้งก่อน/หลังชำระ) และ **ทุกสถานะ order รวม `Cancelled`** —
  write path SHALL NOT gate on `Order.Status`.
- 3.5 WHEN ค่า insurance-reference ใดๆ ถูกสร้างหรือแก้ไข, THE SYSTEM SHALL เขียน audit record
  (actor, line, timestamp, field ที่เปลี่ยน) ทุกครั้ง.
- 3.6 IF actor ไม่มี permission ที่ต้องใช้, THEN THE SYSTEM SHALL ปฏิเสธด้วย 403 (Forbidden) และไม่เขียน
  ค่าใดๆ.
- 3.7 IF `NetPremium` > `GrossPremium`, THEN THE SYSTEM SHALL ปฏิเสธคำขอด้วย 400.
- 3.8 IF `NetPremium` หรือ `GrossPremium` มี currency ไม่ใช่ `THB`, THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
  (subsumes การบังคับให้สองค่าสกุลเงินตรงกัน).
- 3.9 IF `ReferenceNumber` ว่างแต่มีการตั้ง `ReferenceNumberType`, THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
  (type กับ value ต้องมาคู่กัน).
- 3.10 IF `ReferenceNumber` มีค่าแต่ `ReferenceNumberType` ว่าง, THEN THE SYSTEM SHALL ปฏิเสธด้วย 400
  (คู่กันสองทิศ — symmetric กับ 3.9; ไม่ default type เองเพราะทุกค่า external-sourced).
- 3.11 IF `EndorsementNumber` หรือ `RenewalReminderNumber` มีค่าโดยไม่มี `ReferenceNumber` (+
  `ReferenceNumberType`), THEN THE SYSTEM SHALL ปฏิเสธด้วย 400 (สลักหลัง/ใบเตือนต่ออายุต้องแนบกรมธรรม์
  ที่มีอยู่).
- 3.12 IF ตั้ง `NetPremium` หรือ `GrossPremium` เพียงตัวเดียว (ไม่มาเป็นคู่), THEN THE SYSTEM SHALL
  ปฏิเสธด้วย 400 (both-or-neither — ทำให้ invariant `Net <= Gross` ใน 3.7 enforce ได้ทุกครั้ง).
- 3.13 WHEN Admin และ Producer เขียน line เดียวกันพร้อมกัน, THE SYSTEM SHALL ใช้ last-write-wins
  (ไม่มี optimistic concurrency/row-version) โดยทุก write ถูก audit (REQ-3.5) — recovery ผ่าน audit trail.

## REQ-4: Policy report read model (the table)

**User Story:** As an admin or producer, I want a table of sold policies with insurer references and
statuses, so I can track every insured person and their premium settlement.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL มี read endpoint คืน **รายการ line ข้าม order** พร้อม column: ชื่อ-นามสกุลผู้เอา
  ประกัน, `InsuranceCategory`, `ReferenceNumberType`, `ReferenceNumber`/`EndorsementNumber`/
  `RenewalReminderNumber`, `InsuredObjectReference`, `NetPremium`, `GrossPremium`,
  `PremiumRemittanceStatus` (+`DeductedAt`), และสถานะการชำระเงิน.
- 4.2 THE SYSTEM SHALL เปิด read นี้บน **2 plane**: Admin (เห็นข้าม merchant) และ MerchantUser
  (เห็นเฉพาะ merchant ของตน).
- 4.3 THE SYSTEM SHALL คำนวณ column "สถานะการชำระเงิน" เป็น **read projection ที่ derive จาก
  `Order.Status`** (`AwaitingPayment`→รอชำระเงิน, `Paid`→ชำระสำเร็จ, `Cancelled`→ยกเลิก) โดย
  **ไม่เพิ่ม field payment-status ต่อ line และไม่แตะ payment engine**.
- 4.4 THE SYSTEM SHALL รองรับ filter (อย่างน้อย: merchant, ช่วงเวลา, `PremiumRemittanceStatus`,
  สถานะการชำระเงิน) และ paging.
- 4.5 THE SYSTEM SHALL mask `InsuredIdNumber` บน read นี้ (mirror pattern เดิมของ list/summary).
- 4.6 THE SYSTEM SHALL **ไม่ mask** `ReferenceNumber`/`EndorsementNumber`/`RenewalReminderNumber`/
  `InsuredObjectReference` (ไม่ถือเป็น secret เท่าเลขบัตรประชาชน).
- 4.7 WHERE line ยังไม่มีข้อมูล external (REQ-1.7), THE SYSTEM SHALL คืน **เฉพาะ external-reference
  column** เป็นค่าว่าง (เช่น ref number = ว่าง, สถานะตัดชำระ = N/A) แทนที่จะซ่อน row; column "สถานะการ
  ชำระเงิน" **ไม่ว่าง**เสมอ (derive จาก `Order.Status` ตาม REQ-4.3).

## REQ-5: Seed / demo data

**User Story:** As a developer/QA, I want sample policy-reference rows in dev/demo, so the report renders
like the target UI.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL seed ตัวอย่าง line ที่มีข้อมูล insurance-reference ครบ (รวมกรณี Motor:
  ทะเบียนรถ + ภาคสมัครใจ/ภาคบังคับ, และกรณีเบี้ยตัดชำระแล้ว/ยังไม่ตัด) ใน dev/demo dataset.
- 5.2 THE SYSTEM SHALL ครอบคลุมตัวอย่างที่ให้ report แสดงทั้งแถวที่ยังไม่ป้อนข้อมูล (สถานะว่าง) และแถว
  ที่ป้อนครบ (mirror รูปต้นแบบ).

## REQ-6: Preserved constraints (guardrails)

**User Story:** As the platform owner, I want this feature to not turn the platform into an insurer, so the
regulatory/product thesis stays intact.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL NOT แก้ `Order.MarkPaid` state machine, `OrderStatus` lifecycle, หรือ verify
  amount/currency invariant เดิม (insurance-pivot REQ-3.1 ยังผลบังคับ).
- 6.2 THE SYSTEM SHALL NOT แก้ `Payments.Domain.Session`/`SessionStatus`, `IPspAdapter`, หรือ
  webhook-confirm pipeline (insurance-pivot REQ-3.2 ยังผลบังคับ).
- 6.3 THE SYSTEM SHALL NOT generate เลขกรมธรรม์เอง, ออก policy document/PDF, หรือมี issuance/
  underwriting workflow — ทุกเลขและสถานะ **ป้อนจากภายนอก** เท่านั้น (ADR-1).
- 6.4 THE SYSTEM SHALL NOT เพิ่ม claims / commission / reinsurance workflow.
- 6.5 THE SYSTEM SHALL NOT ทำ bulk/automated import จากระบบภายนอกใน spec นี้ (แยก spec) — write path
  ของ spec นี้เป็น manual per-record เท่านั้น.

## REQ-7: Rename OrderLine -> OrderItem (folded 2026-07-23)

**User Story:** As the team, I want the order line-item concept named `OrderItem` (mirror
`Carts.Domain.Items.Item`), so naming is consistent ตลอด funnel (Cart Item -> Checkout Item -> Order Item)
แทน `OrderLine` ที่ drift จาก intent เดิม (v5: "OrderItems ไม่ใช่ OrderLines"). Behavior-preserving.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL rename entity `Orders.Domain.Lines.Line` -> `Orders.Domain.Items.Item` + sibling
  (`OrderLineInput` -> `OrderItemInput`, read models `OrderLine*` -> `OrderItem*`, `RevealAudit` namespace
  `Lines` -> `Items`) ตาม naming law L1-L8.
- 7.2 THE SYSTEM SHALL rename DB objects: table `shop.OrderLines` -> `shop.OrderItems`,
  `shop.OrderLineRevealAudits` -> `shop.OrderItemRevealAudits`, column `OrderLineId` -> `OrderItemId`
  (forward migration, behavior-preserving).
- 7.3 THE SYSTEM SHALL rename sibling checkout: `Checkouts.Domain.Lines.Line` -> `Checkouts.Domain.Items.Item`,
  table `shop.CheckoutSessionLines` -> `shop.CheckoutSessionItems`, `CheckoutLineInput` -> `CheckoutItemInput`.
- 7.4 THE SYSTEM SHALL NOT เปลี่ยน behavior/state-machine/verify-logic ใด ๆ — pure rename; route ที่มี
  `/lines/` segment (ถ้ามี) เปลี่ยนเป็น `/items/` เป็น deliberate contract change (route flat, big-bang ไม่ alias).
- 7.5 THE SYSTEM SHALL retire identifier เก่า (`OrderLine`, order-line `Line`, `CheckoutSessionLine`,
  `CheckoutLineInput`, `OrderLineId`) ใน rename gate (`scripts/check-rename-identifiers.sh`) กันชื่อเก่าโผล่กลับ.
- 7.6 WHERE เป็น L8 external contract (integration-event `Contracts.CheckoutConfirmedLine`, config key,
  OpenAPI scheme id), THE SYSTEM SHALL rename เฉพาะเมื่อ deliberate + review แยก — ไม่ใช่ผลพลอยได้จาก sweep.
- 7.7 THE SYSTEM SHALL ให้ artifact ใหม่ของ REQ-1..4 ใช้ชื่อ OrderItem: entity `ItemPolicy`, table
  `OrderItemPolicies`/`OrderItemPolicyAudits`, endpoint `/orders/{orderId}/items/{itemId}/policy` (rename REQ ต้องเสร็จ
  ก่อน/พร้อมกับ feature เพราะ `ItemPolicy` ผูกกับ `OrderItem`).

## Edge Cases & Open Questions

**Edge cases (ต้องถูกจัดการ):**
- ผู้เอาประกันคนเดียว/รถคันเดียวมีทั้ง line ภาคสมัครใจและภาคบังคับ (พ.ร.บ.) — 2 line แยก (ตามรูป row 1 vs 6).
- Line ที่มี `ReferenceNumberType=NotificationNumber` (เลขรับแจ้ง) แล้วภายหลังได้เลขกรมธรรม์จริง — แก้ผ่าน
  write path (mutable, REQ-3.4) เปลี่ยน type + number.
- `GrossPremium` (THB) ต่างจาก **จำนวน** `Line.UnitPrice`/`Order.Amount` — เป็น reference data แยก
  ไม่บังคับ reconcile กับยอดที่ชาร์จ (premium fix THB ตาม REQ-3.8; ดู open question reconciliation).
- รูปต้นแบบมีช่อง "สถานะการชำระเงิน" ว่างใน row 1-6 — คือ row ที่ยังไม่ป้อนข้อมูล external ไม่ใช่ payment
  column ที่ว่าง; ในระบบ payment column derive จาก `Order.Status` เสมอ (REQ-4.3/4.7).
- Producer พยายามแก้ line ของ merchant อื่น — 403 (REQ-3.3/3.6).

**Open questions (ตัดสินใน design):**
- **Entity shape:** เพิ่ม field บน `Orders.Domain.Lines.Line` เดิม vs สร้าง entity `PolicyRecord` แยก
  (ผูก 1:1 กับ line). ADR-1 อนุญาตทั้งสองทาง.
- **Reconciliation:** `GrossPremium` (external) ควร validate ให้ตรง/ใกล้เคียง `Line.UnitPrice` หรือ
  `Order.Amount` หรือไม่ (ตอนนี้ไม่บังคับ) — ตัดสินตอน design.
- **Filter/paging engine:** ใช้ convention `search-filter-sort` (JSON-DSL SFS) ที่ documented ไว้หรือ
  paging แบบง่ายก่อน.
- **Permission keys:** ชื่อ `iam.*` key ใหม่สำหรับ read/write บนแต่ละ plane — ตัดสินตอน design ให้เข้ากับ
  catalog เดิม.
- **Checkout snapshot:** external reference data ป้อนหลังการขาย จึง **ไม่** ผ่าน checkout snapshot
  (`Checkouts.Domain.Lines.Line`) — ยืนยันว่า checkout path ไม่ต้องแตะ (ต่างจาก field snapshot เดิม).
- **String length/format:** max length + รูปแบบ (regex/allowed chars) ของ `ReferenceNumber`/
  `EndorsementNumber`/`RenewalReminderNumber`/`InsuredObjectReference` — ปล่อย design (N3).

## Analysis Findings Log

> Anchor: repo HEAD `44a5d47` (requirements.md ยัง uncommitted draft — ไม่มี per-file hash).
> Advisor: Fable 5. ทุก finding AGREE กับ ★ recommendation. re-run `/spec-analyze` ให้ข้ามรายการเหล่านี้.
> Scope amendment 2026-07-23: fold rename `OrderLine`->`OrderItem` (code + DB table + CheckoutLine sibling) = REQ-7 (user decision).

| Finding | Decision | Applied to |
|---|---|---|
| F1 payment-status blank vs derive | (a) derive จาก Order เสมอ; blank ในรูป = row ยังไม่ป้อน external | REQ-4.7 (reword), edge case |
| F2 THB fixed | (a) premium SHALL เป็น THB | REQ-1.6, REQ-3.8 (rewrite) |
| F3 endorsement/renewal ต้องมี base ref | (a) require ReferenceNumber ก่อน | REQ-3.11 |
| F4 value-set/type-empty | (a) reject 400 (คู่กันสองทิศ) | REQ-3.10 |
| F5 net/gross pair | (a) both-or-neither | REQ-1.6, REQ-3.12 |
| F6 DeductedAt future + clear | (a) reject future 400 + clear+audit on revert | REQ-2.5, REQ-2.6 |
| F7 concurrency | (a) last-write-wins + audit (no row-version) | REQ-3.13 |
| F8 unset enum | (a) nullable (null = unset) | REQ-1.11 |
| F9 write on Cancelled order | (a) allow; no Order.Status gate | REQ-3.4 |
| N1 DeductedAt source | client-supplied external date (ไม่ใช่ server timestamp) | REQ-2.2 |
| N2 duplicate ReferenceNumber | อนุญาต (ไม่มี unique constraint) | REQ-1.10 |
| N3 string length/format | deferred → design | Open Questions |
