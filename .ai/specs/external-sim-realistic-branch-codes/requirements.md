# Requirements: external-sim-realistic-branch-codes

> Status: approved 2026-08-02
> Scope: `dbo.Documents` ใน simulated `hippodb`/`mammothdb` (`docker/bootstrap/02-external-sim.sql`) — เปลี่ยน `ReferenceBranch` value scheme + ลบคอลัมน์ `BranchCode` ที่ไม่มีใน real contract — อ้าง `docs/reference/vcentralpay-sp-quick-reference.pdf` v1.0 §2/§5.2/§6 และแผนที่ approve `~/.claude/plans/documentno-format-serene-crescent.md`
> Supersedes: `products-sp-gateway` REQ-2.11 (closed spec, ไม่แก้ย้อนหลัง — ดูเหตุผลใน REQ-4 ด้านล่าง)

## Overview

`external-sim-shared-agent-network` รวม `ReferenceBranch` ของ hippodb/mammothdb เป็น roster เดียว
(`77001`-`77006` → `{900,901,902,903,904}`) แต่ค่าที่ได้เรียง +1 ต่อเนื่องจนดูเหมือน index แถวมากกว่า
รหัสสาขาจริง เทียบกับตัวอย่าง DocumentNo จากภาพเอกสารต้นทางที่ผู้ใช้ยกมา งานนี้เปลี่ยนชุดค่าให้กระจาย
สมจริงกว่า (`{301,315,220,335,450}`) โดยไม่แตะกลไกเดิม (ยังผูกกับ `SaleCode`/agent ผ่าน `CROSS APPLY`
เหมือนเดิม)

ระหว่างตรวจสอบ ผู้ใช้ชี้เอกสารทางการ `docs/reference/vcentralpay-sp-quick-reference.pdf` §5.2 (Result
set 2, 32 field เต็ม) ว่าไม่มี field ชื่อ `BranchCode` เลย — ตรงกับที่ query จริงไม่เคย `SELECT`
`d.BranchCode` เข้า result set อยู่แล้ว แต่คอลัมน์ `BranchCode` บน `dbo.Documents` (ไม่เคยถูก filter/
output จริง) ยังถูก seed ทิ้งไว้เป็น dead artifact ตามการตัดสินใจ `products-sp-gateway` REQ-2.11
("assumption" รอ SP owner ยืนยัน ไม่เคยถูกยืนยันจริง) งานนี้ลบคอลัมน์นั้นออก — `ReferenceBranch` (มีจริง
ใน §5.2) เป็นตัวแทนรหัสสาขาแต่ผู้เดียว

`@BranchCode` ยัง**เป็น required input parameter จริงตาม PDF §2** (validate ห้ามว่าง, error 50004) —
ไม่เปลี่ยน สิ่งที่ลบคือคอลัมน์บน table เท่านั้น การเพิ่ม filter semantics จริง (`WHERE ReferenceBranch =
@BranchCode`) เป็นคนละเรื่อง — **out of scope โดยเจตนา** เพราะ production adapter ยัง hardcode
`@BranchCode = "000"` คงที่ (`SpDocumentOptions.cs:20-24`, รอ wiring "actor's branch claim") การเปิด
filter ตอนนี้จะทำให้ `GET /products` คืนแถวว่างเปล่าทุก request ทันที

## REQ-1: ReferenceBranch value scheme ใหม่

**User Story:** As a maintainer ของ simulated upstream DB, I want ค่า `ReferenceBranch` ที่กระจายแบบ
สมจริง (ไม่เรียง +1 ต่อเนื่อง), so that DocumentNo/PolicyNumber ที่ generate ออกมาดูเหมือนรหัสสาขาจริง
แทนที่จะดูเหมือน row index

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL map `SaleCode` → `ReferenceBranch` ผ่าน `CROSS APPLY` CASE expression เหมือน กลไกเดิม (ไม่เปลี่ยนโครงสร้าง query)
- 1.2 THE SYSTEM SHALL ใช้ชุดค่า `ReferenceBranch` ใหม่ 5 ค่า: `301` (SaleCode `77001`,`77006`), `315` (`77002`), `220` (`77003`), `335` (`77004`), `450` (`77005`)
- 1.3 THE SYSTEM SHALL คงความยาว `ReferenceBranch` เป็น 3 หลักเสมอ (ตรง `varchar(3)` ใน §5.2)
- 1.4 THE SYSTEM SHALL ใช้ CASE expression เดียวกัน byte-identical ระหว่าง hippodb block และ mammothdb block ใน `docker/bootstrap/02-external-sim.sql`
- 1.5 THE SYSTEM SHALL NOT เปลี่ยนค่า `PolicyBranch`/`SaleFullName`/`BrokerCode`/`BrokerName` ที่ผูกกับ `SaleCode` เดียวกันใน `CROSS APPLY` เดียวกัน — เปลี่ยนเฉพาะค่า `ReferenceBranch` เท่านั้น

## REQ-2: Non-collision กับ field อื่นในไฟล์เดียวกัน

**User Story:** As a maintainer, I want ค่า `ReferenceBranch` ใหม่ไม่ชนความหมายกับ field คงที่อื่นในไฟล์
เดียวกัน, so that ไม่มีใครอ่าน seed แล้วสับสนว่าค่าตัวเลขหมายถึงอะไร

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL เลือกค่า `ReferenceBranch` ที่ไม่ชนกับ `ReferencePre = '900'` (marker คงที่เฉพาะ `DocumentType = 'ENDORSEMENT'`, คนละ field คนละความหมาย)
- 2.2 THE SYSTEM SHALL เลือกค่าที่ไม่ชนกับ `SaleCode` 5 หลัก (`77xxx`) หรือ `PolicyYear` 2 หลัก (`69`/`26`)
- 2.3 WHERE คอลัมน์ `BranchCode` เดิมถูกลบตาม REQ-4, THE SYSTEM SHALL ไม่ต้องหลีกเลี่ยงค่าเดิมของมัน (`100/200/300/400`) อีกต่อไปเพราะไม่มีคอลัมน์นั้นเหลืออยู่แล้ว
- 2.4 THE SYSTEM SHALL เลือกค่า `ReferenceBranch` ที่ไม่ทำให้ `PolicyYear + ReferenceBranch` (`'69'`/`'26'` ต่อด้วยค่าใหม่ เช่น `'69301'`) เกิด substring `'91'` หรือ `'80'` — ค่า marker ที่ `external-sim-documentno-format` ฝังไว้ใน `PolicySequenceNo` ของ axis row จริงบน hippodb (`SpDocumentContractTests.The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL` ใช้ `@SearchText = '91'` แยกแยะ row ผ่าน `DocumentNo LIKE`) — พบจาก implementation-time test failure: `101`/`115` (ขึ้นต้นด้วย `1`) ต่อท้าย `'69'` กลายเป็น `'691xx'` ซึ่งมี `'91'` ทำให้ทุก row ของ SaleCode นั้นๆ false-match แทนที่จะ match เฉพาะ 4 axis row ที่ตั้งใจ; ค่าที่เลือกจริง (`301`/`315`/`220`/`335`/ `450`) ผ่านเกณฑ์นี้แล้ว (verify สดว่า `SearchText='91'` กลับมาแค่ 4 sequence ที่ตั้งใจก่อน mark task 2)

## REQ-3: DocumentNo/PolicyNumber formula ไม่เปลี่ยนโครงสร้าง

**User Story:** As a consumer ของ SP output, I want DocumentNo/PolicyNumber/ApplicationNumber/
PreviousPolicyNumber ยัง compute-forward จาก field เดิมเหมือนเดิม, so that การเปลี่ยนค่า
`ReferenceBranch` ไม่ทำให้ formula เพี้ยน

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL คง formula `CONCAT(PolicyYear, rb.ReferenceBranch, '/', ab.Abbrev, '/', d.PolicySequenceNo)` (และ formula พี่น้องที่ใช้ `ReferenceBranch` เดียวกัน) โดยแทนที่แค่ตัวเลข `ReferenceBranch` ที่ไหลเข้ามา ไม่แก้โครงสร้าง concatenation

## REQ-4: ลบคอลัมน์ BranchCode ออกจาก dbo.Documents

**User Story:** As a maintainer, I want ลบคอลัมน์ `BranchCode` ที่ไม่มีจริงใน PDF §5.2 ออกจาก schema
จำลอง, so that seed ไม่มี dead artifact ที่ทำให้คนอ่านเข้าใจผิดว่ามี field นี้จริงในระบบต้นทาง

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL ลบ column definition `BranchCode varchar(3) NULL` ออกจาก `CREATE TABLE dbo.Documents` ทั้ง hippodb และ mammothdb block
- 4.2 THE SYSTEM SHALL ลบ `BranchCode` ออกจาก column list และ value expression ที่คู่กันใน axis-row `INSERT ... VALUES` และ generated-row `INSERT ... SELECT` ทั้งสองฝั่ง
- 4.3 THE SYSTEM SHALL เขียนใหม่ header comment "DELIBERATE DEVIATIONS" #2 (บรรทัด ~29-35) ให้สะท้อน ความจริงใหม่: `@BranchCode` parameter ยัง required+validate ตาม PDF §2 แต่ไม่มีคอลัมน์ backing เพราะ output จริง (§5.2) ไม่มี field นี้ — ถ้าต้อง filter จริงในอนาคตให้ target `ReferenceBranch`
- 4.4 THE SYSTEM SHALL ระบุใน spec นี้ว่า supersede `products-sp-gateway` REQ-2.11 ("BranchCode validate-only เป็น assumption") ด้วยเหตุผลที่ยืนยันแล้วจาก PDF §5.2 — โดยไม่แก้ไฟล์ `products-sp-gateway/requirements.md` ย้อนหลัง (closed spec = historical record ตาม convention repo)
- 4.5 IF migration/schema tool อื่นอ้างถึง `dbo.Documents.BranchCode` ของ simulated DB (คนละคอลัมน์กับ
  `shop.Products.BranchCode` ที่ถูกลบไปแล้วใน `products-sp-53-alignment`) THEN THE SYSTEM SHALL รายงาน
  เป็น edge case ก่อนลบ — จาก grep เบื้องต้นยืนยันแล้วว่าไม่มี

## REQ-5: @BranchCode SP parameter ไม่เปลี่ยน (non-regression)

**User Story:** As a consumer ของ SP contract, I want `@BranchCode` input parameter ยัง required +
validate เหมือนเดิมทุกประการ, so that contract ฝั่ง input ยังตรง PDF §2 แม้คอลัมน์ output ถูกลบ

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL คง `@BranchCode` parameter declaration ใน `usp_Motor_SearchDocument` และ `usp_NonMotor_SearchDocument` โดยไม่เปลี่ยน type/nullability
- 5.2 THE SYSTEM SHALL คง trim logic ของ `@BranchCode` ไม่เปลี่ยน
- 5.3 IF `@BranchCode` เป็นค่าว่าง THEN THE SYSTEM SHALL `THROW 50004` เหมือนเดิมทุกประการ (ข้อความ error ไม่เปลี่ยน)

## REQ-6: ห้ามเพิ่ม filter semantics ให้ @BranchCode (out of scope)

**User Story:** As a maintainer, I want งานนี้ไม่แตะ filter logic ของ `@BranchCode`, so that ไม่เผลอทำ
`GET /products` คืนแถวว่างเปล่าทุก request (เพราะ production ยัง hardcode `@BranchCode = "000"` คงที่
ไม่ตรง `ReferenceBranch` ค่าไหนเลย)

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL NOT เพิ่ม `WHERE ... ReferenceBranch = @BranchCode` หรือ predicate ใด ๆ ที่ทำให้ `@BranchCode` มีผลต่อผลค้นหา ใน scope งานนี้
- 6.2 THE SYSTEM SHALL คงพฤติกรรม validate-only ของ `@BranchCode` ไว้จนกว่าจะมี spec แยกที่ wiring "actor's branch claim" เข้ากับ auth ก่อน

## REQ-7: Self-check ทั้งสองฝั่งผ่านโดยไม่ต้องแก้ logic

**User Story:** As a maintainer, I want cross-database identity self-check (`EXCEPT`-based) และ
roster-completeness self-check ยังผ่านหลังเปลี่ยนค่า, so that ยืนยันว่า hippodb/mammothdb ยัง sync กัน

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL ผ่าน cross-database identity self-check โดยไม่ต้องแก้ query ของ self-check เอง (เพราะเทียบสดจากทั้งสอง database ไม่มี hardcoded ค่าเดิม)
- 7.2 THE SYSTEM SHALL ผ่าน roster-completeness/`ShowName`→`SaleCode` invariant self-check โดยไม่ต้อง แก้ query ของ self-check เอง

## REQ-8: Re-pin test literal จาก live query เท่านั้น

**User Story:** As a maintainer, I want ค่า literal/comment ใน integration test ที่อ้างอิง
`ReferenceBranch`/`PolicyYearBranch`/`DocumentNo` ถูก re-pin จาก database ที่ reseed จริงเท่านั้น, so
that ไม่มี hand-derived value ผิดหลุดเข้า test

**Acceptance Criteria (EARS):**
- 8.1 THE SYSTEM SHALL re-pin `SpDocumentContractTests.cs`'s `MotorSide.PolicyYearBranch` และ `NonMotorSide.PolicyYearBranch` จากค่าที่ query ได้จริงหลัง reseed (ไม่ hand-derive จากตาราง REQ-1)
- 8.2 THE SYSTEM SHALL re-pin `SpDocumentGatewayIntegrationTests.cs`'s `AxisReferenceBranch`, `AxisPolicyNumber`, `AxisDocumentNo`, `PaidPolicyNumber` (และ comment ที่อธิบายค่าเหล่านี้) จาก live query เท่านั้น
- 8.3 THE SYSTEM SHALL NOT เปลี่ยน `TotalRows`/`TotalPages`/`LastPageRows` (ReferenceBranch ไม่กระทบ predicate การมองเห็นแถว)
- 8.4 THE SYSTEM SHALL แก้ comment ใน `SpDocumentContractTests.cs` (`Branch_code_is_validated_but_never_filters`, บรรทัด ~392-393) และ `SpDocumentGatewayIntegrationTests.cs` (`The_branch_code_is_sent_from_options_and_only_validates`, บรรทัด ~199) ให้สะท้อนว่าไม่มีคอลัมน์ `BranchCode` แล้ว โดยไม่เปลี่ยน assertion logic (ยังส่ง `@BranchCode` เป็น arbitrary string `"100"`/`"400"`/`"999"` เหมือนเดิม — พิสูจน์ validate-only ได้แน่นขึ้นเพราะไม่มี column ให้ match แม้แต่ ทางทฤษฎี)

## REQ-9: อัปเดตเอกสารประกอบ (append-only บน closed spec)

**User Story:** As a future reader, I want ตัวอย่างใน `docs/reference/products.md` และ footnote ใน
`products-sp-gateway/HANDOFF.md` สะท้อนค่าใหม่, so that เอกสารไม่ตกยุคเหมือนที่เคยเกิดกับ PR #160

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL อัปเดตตัวอย่าง `DocumentNo` ใน `docs/reference/products.md` (บรรทัด ~160) และ ตัวอย่าง `ReferenceBranch` แถวเดี่ยว ๆ (บรรทัด ~162, ปัจจุบัน `001` ซึ่งผิดอยู่แล้วตั้งแต่ก่อนงานนี้ ไม่ ตรงทั้ง scheme เดิม/ใหม่) ให้ตรงค่าที่ query ได้จริงหลัง reseed ทั้งคู่
- 9.2 WHERE `products-sp-gateway/HANDOFF.md` เป็น closed spec's HANDOFF, THE SYSTEM SHALL เพิ่ม footnote ใหม่ (append เท่านั้น ไม่ rewrite ของเดิม ตาม pattern commit `9868cf4`) ชี้ไปยัง `external-sim-realistic-branch-codes/` เป็น current-state reference
- 9.3 THE SYSTEM SHALL NOT แก้ `products-sp-gateway/requirements.md`, `design.md`, หรือ `tasks.md`

## REQ-10: Definition of Done gate

**User Story:** As a maintainer, I want งานนี้ผ่าน gate เดียวกับ spec พี่น้อง (`products-sp-gateway`
REQ-11.x), so that การเปลี่ยนแปลงไม่หลุด regression และ traceability ยังครบ

**Acceptance Criteria (EARS):**
- 10.1 WHEN งานทั้ง spec เสร็จ, `dotnet build pol-core.slnx -warnaserror` SHALL ผ่าน 0 error / 0 warning
- 10.2 WHEN งานทั้ง spec เสร็จ, `source .env.integration && dotnet test
  tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"` SHALL เขียวทั้งหมด
  ไม่มี `.only`/`.skip` ค้าง
- 10.3 WHEN งานทั้ง spec เสร็จ, `dotnet test pol-core.slnx` (full solution) SHALL เขียวทั้งหมด
- 10.4 WHEN งานทั้ง spec เสร็จ, `bash scripts/spec-trace.sh external-sim-realistic-branch-codes` SHALL
  พิมพ์บรรทัด `OK:`
- 10.5 WHEN `docker compose up pol-db-init` รันหลัง reseed, THE SYSTEM SHALL ผ่านโดยไม่มี `THROW` (รวม self-check ตาม REQ-7)

### Findings log — /spec-analyze รอบ 1 (anchor: HEAD `9868cf4`, ไฟล์ยังไม่เคย commit; audit 2026-08-02)

| # | ประเด็น | ตัดสิน |
|---|---|---|
| F1 | REQ-1 ไม่การันตี PolicyBranch/SaleFullName/BrokerCode/BrokerName คงเดิม | เพิ่ม REQ-1.5 non-regression |
| F2 | ไม่มี REQ เทียบเท่า REQ-11.x (DoD gate) ของ spec พี่น้อง | เพิ่ม REQ-10 |
| F3 | `docs/reference/products.md:162` (ตัวอย่าง ReferenceBranch เดี่ยว ๆ `001`) ผิดอยู่แล้วก่อนงานนี้ ไม่อยู่ใน REQ-9.1 เดิม | รวมเข้า REQ-9.1 |
| F4 | REQ-2 enumerate เฉพาะ 3 field เสี่ยงชน (ReferencePre/SaleCode/PolicyYear) — ยังมี field อื่นที่ใช้ ReferenceBranch หรือไม่ | ยืนยันแล้วจาก grep สด (DocumentNo/PolicyNumber/ApplicationNumber/PreviousPolicyNumber ล้วน reuse 3 field เดิม ไม่มี literal ใหม่) — ไม่มี field เสี่ยงเพิ่ม ไม่ต้องแก้ REQ |
| F5 | REQ-7 อ้างว่า self-check ไม่ hardcode ค่า `900`-family — ยืนยันจริงหรือไม่ | ยืนยันแล้วจาก grep สด (`EXCEPT`-based cross-db check เทียบสดทุกฝั่ง ไม่มี literal `900`/`901`/... hardcode ในตัว self-check เอง) |

## Edge Cases & Open Questions

- **Row เดิมที่เคย reference ค่า `901` เป็น "90001 เดิม"**: เหตุผลที่เคยบันทึกไว้ตอนเลือก axis row จะ
  moot (กลายเป็น `315`) แต่กลไก derive จาก `SaleCode` ไม่พัง — implementer ต้อง verify จาก live DB ไม่
  เชื่อ note เก่าใน closed spec
- **`@BranchCode` filter semantics ยังเปิดค้าง** (มาจาก `products-sp-gateway` REQ-2.11 open question) —
  spec นี้ไม่ปิดคำถามนั้น แค่ระบุชัดว่าไม่ implement ในรอบนี้ (REQ-6) รอ SP owner ยืนยัน + auth wiring
  เป็น spec แยกในอนาคต
- **`shop.Products.BranchCode`** (คอลัมน์ของ application's own domain table ใน EF migrations
  `20260730072057_ProductsInsuranceDocument.cs`/`20260730113459_ProductsSp52Alignment.cs`) — คนละคอลัมน์
  คนละความหมายกับ `dbo.Documents.BranchCode` ที่ spec นี้ลบ (ถูกลบไปแล้วจริงตาม
  `products-sp-53-alignment` REQ-1.1) — ไม่เกี่ยวข้อง ไม่ต้องแตะ
