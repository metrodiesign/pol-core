# Requirements: ทำให้วันที่ของ sim seed เสถียร (integration suite ไม่แดงเพราะเวลาเดินไป)

> Status: approved (2026-08-06) — ตัดสินแล้ว: D1 = A+C (Refresh อัตโนมัติก่อน suite เพื่อให้ anchor = วันนี้เสมอ + test เลิกใช้ `DateTime.Today` แล้วอ่าน anchor จากข้อมูล sim จริง), D2 = A (UTC ทุกฝั่ง), D3 = A (xUnit fixture เป็นคน re-seed — fail-safe ต่อคนรัน `dotnet test` ตรง ๆ ตาม REQ-4.4)

integration test ที่ยิง sim upstream (`hippodb`/`mammothdb`) แดงเองเมื่อ container เปิดค้างข้ามวัน
เอกสารนี้กำหนดว่า "เสถียร" แปลว่าอะไร และอะไรห้ามหายไปพร้อมกับการแก้

## Overview

ปัญหาไม่ใช่ bug ในโค้ดของ feature ใด — verifier ของงาน `products-external-source-of-truth` ยืนยันอิสระ
5 รอบว่า `git diff` ของไฟล์เทสต์ที่แดงว่างเปล่าเทียบทั้ง base และ HEAD
(`.pipeline/products-external-source-of-truth/tests-t5.md:186-206`)

### นาฬิกา 3 เรือนที่ไม่ตรงกัน

| เรือน | คำนวณเมื่อไร | หน่วย | ที่มา |
|---|---|---|---|
| วันที่ในข้อมูล seed | ครั้งเดียวตอน bootstrap container | container-local | `02-hippo-sim.sql:373`, `03-mammoth-sim.sql:342` |
| window ของ SP | ทุกครั้งที่เรียก SP | container-local | `02-hippo-sim.sql:232`, `03-mammoth-sim.sql:227` |
| ค่าที่ test คาด | ทุกครั้งที่รัน test | host-local | `SpDocumentContractTests.cs:465`, `:571-572` |

เรือนที่ 1 หยุดนิ่ง เรือนที่ 2 กับ 3 เดิน — พอเดินไม่พร้อมกันเมื่อไร assertion ก็หลุด

### หลักฐานที่วัดได้จริง (2026-08-06)

```
docker inspect pol-hippo-db  -> StartedAt 2026-08-05T04:05:41Z
date -u                      -> Thu Aug  6 04:45 UTC 2026
SELECT CURRENT_TIMEZONE()    -> (UTC) Coordinated Universal Time   # GETDATE() == GETUTCDATE()
hippodb   visible=41 (test ตรึงไว้ 42)   row 8000013 StartDate=2026-02-05  vs  floor=2026-02-06
mammothdb visible=40 (ตรงกับที่ตรึงไว้)  min StartDate=2025-12-03           vs  floor=2026-02-06
```

แถว `8000013` ถูก seed ที่ `DATEADD(month, -6, @today)` พอดีเป๊ะ (`02-hippo-sim.sql:419-420`) จึงหลุด window
ตั้งแต่วันที่สอง — นั่นคือ 1 แถวที่หายไปจาก 42

### ทำไม Motor แดงก่อน และทำไม Non-Motor ไม่ปลอดภัย

verifier บันทึกว่า 12 fail อยู่ที่ `SpDocumentContractTests` 10 ตัว + `SpDocumentGatewayIntegrationTests`
2 ตัว "ฝั่ง Motor (hippodb) ล้วน" — เพราะ hippodb มีแถวขอบพอดีและมี window สองชั้น (RENEWAL ตัดที่ `EndDate`)
ส่วน mammothdb ตัดด้วย `StartDate` อย่างเดียว และทุกแถวมี margin >= 122 วัน

Non-Motor ไม่ได้ภูมิคุ้มกัน แค่ยังไม่ถึงคิว — assertion ที่ผูกกับ `DateTime.Today` ตรง ๆ
(`SpDocumentContractTests.cs:471-473`) เป็น `[Theory]` ที่ยิงทั้งสองฝั่ง

### ทำไม CI เขียว และทำไมเขียวแบบเสี่ยง

CI bootstrap sim สด ๆ ก่อนรัน test ในงานเดียวกัน (`.github/workflows/ci.yml:215-228`) และ runner เป็น UTC
จึงไม่มี drift ให้เห็น — แต่ถ้า run ใดคร่อมเที่ยงคืน UTC ระหว่างขั้น bootstrap กับขั้น test ก็แดงแบบเดียวกัน

### สิ่งที่อยู่นอกขอบเขต

- ไม่แตะ contract ของ SP ที่ upstream ตัวจริงต้องทำตาม (จำนวนคอลัมน์ ชื่อ parameter รูป result set)
- ไม่แก้รูปข้อมูล seed (200 แถว/ฝั่ง, roster 6 SaleCode, DocumentNo format)
- ไม่แตะ prod หรือฐานข้อมูล `VCentralPay` schema

## REQ-1: ผลของชุดเทสต์ไม่ขึ้นกับอายุ container และ timezone ของเครื่อง

**User Story:** ในฐานะนักพัฒนา ผมอยากรัน integration suite เมื่อไรก็ได้แล้วได้ผลเดิม
เพื่อที่ผลแดงจะแปลว่าโค้ดพัง ไม่ใช่แปลว่าเครื่องเปิดค้างมานาน

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL ให้ `SpDocumentContractTests` และ `SpDocumentGatewayIntegrationTests` ผ่านครบ
  โดยไม่ขึ้นกับจำนวนวันที่ container ของ sim เปิดค้างอยู่
- 1.2 WHERE เครื่องที่รันตั้ง timezone ใดก็ตาม (UTC, `Asia/Bangkok`, หรืออื่น) THE SYSTEM SHALL
  ให้ผลของทั้งสองชุดเหมือนกันทุกประการ
- 1.3 WHEN เวลาข้ามเที่ยงคืนระหว่างขั้น bootstrap กับขั้น assert THE SYSTEM SHALL ยังให้ผลเดิม
- 1.4 IF anchor ของข้อมูล seed ไม่ตรงกับ anchor ที่ SP ใช้ตอน query THEN THE SYSTEM SHALL แดงพร้อม
  ข้อความที่ระบุค่าทั้งสองตัว ไม่ใช่ปล่อยให้เห็นแค่ `Expected 42 Actual 41`

## REQ-2: "วันนี้" มีแหล่งความจริงเดียวและหน่วยเดียว

**User Story:** ในฐานะผู้ดูแลชุดเทสต์ ผมอยากให้ทุกฝั่งอ้างวันที่จากที่เดียวกัน
เพื่อไม่ต้องไล่ debug ว่าใครใช้เรือนไหน

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL มี anchor ของ "วันนี้" เพียงจุดเดียวที่ทั้งข้อมูล seed, window ของ SP
  และค่าที่ test คาด อ้างถึงร่วมกัน
- 2.2 THE SYSTEM SHALL ใช้หน่วยเวลาเดียวกันทุกฝั่ง — ห้ามฝั่งหนึ่งเป็น UTC อีกฝั่งเป็น local
- 2.3 THE SYSTEM SHALL ไม่ให้ค่าที่ test คาดขึ้นกับ timezone ของ host — `DateTime.Today`
  ที่ `SpDocumentContractTests.cs:465` และ `:571-572` ต้องไม่เป็นแหล่งความจริงอีกต่อไป
- 2.4 THE SYSTEM SHALL ประกาศหน่วยเวลาที่เลือกไว้เป็นลายลักษณ์อักษรในไฟล์ seed ทั้งสอง
  เพื่อให้คนที่ตั้ง `TZ` ให้ container ในอนาคตเห็นผลกระทบก่อนลงมือ

## REQ-3: search window ของ SP ยังถูกทดสอบจริง ทั้งเคสเข้าและเคสหลุด

**User Story:** ในฐานะเจ้าของ contract กับ upstream ผมอยากให้กฎ window ยังมี test คุ้มครองครบ
เพราะชุดนี้จะกลายเป็น acceptance suite ตอนต่อฐานจริง (`SpDocumentContractTests.cs:10-13`)

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL ยังมี test ที่พิสูจน์ว่าแถวที่อยู่ใน window ถูกคืน — ทั้งกฎ Motor
  (RENEWAL ตัดด้วย `EndDate` ใน `[today, today+2 months)`, `02-hippo-sim.sql:245-249`)
  และกฎ Non-Motor (`StartDate >= DATEADD(month, -6, @today)`, `03-mammoth-sim.sql:238`)
- 3.2 THE SYSTEM SHALL ยังมี test ที่พิสูจน์ว่าแถวที่หลุด window ไม่ถูกคืน ครบ 3 เคส:
  RENEWAL หมดอายุแล้ว, RENEWAL ที่ `EndDate` เกิน 2 เดือน, non-RENEWAL ที่ `StartDate` เก่ากว่า 6 เดือน
- 3.3 THE SYSTEM SHALL ยังมี test ที่พิสูจน์ว่าขอบ 6 เดือนเป็นแบบ inclusive ด้วยแถวที่นั่งบนขอบพอดี
  (`8000013`) — ครอบคลุมโดย `Motor_coverage_start_window_includes_the_row_sitting_exactly_six_months_back`
- 3.4 THE SYSTEM SHALL ยังมี test ที่พิสูจน์ว่า window ถูกตัดสินเป็นราย row เมื่อ `@DocumentType = 'ALL'`
  (`The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL`)
- 3.5 IF การแก้ทำให้ test ข้อใดใน 3.1-3.4 ถูกลบ ปิด หรือแทนที่ด้วย assertion ที่อ่อนกว่าเดิม
  THEN THE SYSTEM SHALL ถือว่างานยังไม่เสร็จ

## REQ-4: ห้ามแก้ด้วยการลดคุณค่าของ test

**User Story:** ในฐานะคนที่ต้องเชื่อผลเขียว ผมอยากให้เขียวแปลว่าถูกจริง
ไม่ใช่แปลว่าเทสต์เลิกตรวจ

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL ไม่ใช้ skip ทุกรูปแบบ (`Skip`, conditional skip, `Trait` ที่ทำให้หลุดจาก CI filter)
  กับ test ที่ได้รับผลกระทบ
- 4.2 THE SYSTEM SHALL คงค่าคาดหวังแบบเป๊ะไว้ — `TotalRows` 42/40 ยังเป็นตัวเลขตายตัว
  และ landmark ที่คาดผลลัพธ์แถวเดียวยังต้องได้แถวเดียว ห้ามเปลี่ยนเป็นช่วงค่าหรือ `NotEmpty`
- 4.3 THE SYSTEM SHALL ไม่คำนวณค่าคาดหวังด้วยการรัน predicate ชุดเดียวกับที่ SP ใช้
  เพราะจะได้ test ที่เห็นด้วยกับ SP เสมอไม่ว่า SP จะผิดแค่ไหน
- 4.4 THE SYSTEM SHALL ไม่บังคับให้มีขั้นตอนมือก่อน `dotnet test` — restart container, `docker compose down -v`
  หรือรัน bootstrap เองไม่นับเป็นทางแก้
- 4.5 WHEN แก้เงื่อนไข window ใน SP ให้ผิดโดยเจตนา (mutation) THE SYSTEM SHALL ทำให้มีอย่างน้อย 1 test แดง

## REQ-5: ผลกระทบข้างเคียงที่ต้องปิดให้ครบ

**User Story:** ในฐานะ orchestrator ผมอยากรู้ว่าการเปลี่ยน anchor กระทบอะไรอีก
เพื่อไม่ให้ไปโผล่เป็น failure ใหม่ในรอบถัดไป

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL ทำให้ทุก `DocumentNo` ที่ `seed-demo.sql` ใส่ไว้ใน cart/order ยังอยู่ใน window
  ตอน checkout — เงื่อนไขที่ `seed-demo.sql:196-200` บันทึกไว้ว่า `26301/POL/000003` ที่ -245 วัน
  เคยทำให้ checkout ตอบ 409
- 5.2 THE SYSTEM SHALL ทำให้ `SeedDemoIntegrationTests` และ `MerchantCatalogueLiveEndpointTests`
  ยังผ่าน — ทั้งสองยิง `LookupAsync` จริงผ่าน window ของ SP
  (`SeedDemoIntegrationTests.cs:109-122`, `MerchantCatalogueLiveEndpointTests.cs:232-234`)
- 5.3 THE SYSTEM SHALL ทำให้ self-check ท้ายไฟล์ seed จับ drift ได้จริง — ปัจจุบันมันใช้ `@today`
  ก้อนเดียวกับข้อมูลที่เพิ่งเขียน (`02-hippo-sim.sql:645-656`, `03-mammoth-sim.sql:586-596`)
  จึงผ่านเสมอ ไม่ว่าข้อมูลจะเก่าแค่ไหน
- 5.4 THE SYSTEM SHALL ทำให้ job `dotnet integration (live SQL 2025)` ยังเขียว และเขียวโดยไม่พึ่งจังหวะ
  ว่า run นั้นไม่คร่อมเที่ยงคืน UTC
- 5.5 THE SYSTEM SHALL ไม่แตะ test ที่ไม่พึ่งวันที่ — `SimCrossInstanceConsistencyTests` (นับ 200/200
  จาก `dbo.Documents` ตรง ๆ) และ `DocumentNoCollationIntegrationTests`
- 5.6 WHERE ขั้นตอนรัน integration test เปลี่ยนไป THE SYSTEM SHALL อัปเดต `docs/runbooks/local-dev-run.md`
  ให้ตรง — ปัจจุบันมันบอกแค่ `docker compose down -v && docker compose up -d`

## Edge Cases

- แถว RENEWAL `9100005` (`EndDate` +100 วัน) ไม่ได้แค่หลุดออก — พอ container เก่าเกินราว 40 วัน
  มันจะ **เข้า** window แทน ทำให้ `TotalRows` เพิ่มขึ้น ไม่ใช่ลดลง
- แถวที่ generate มา 186 แถวใช้ `DaysBack` สูงสุด 170 วัน (`02-hippo-sim.sql:465`) จึงทนได้ราว 11-14 วัน
  ก่อนเริ่มร่วง — อาการจะค่อย ๆ เลวลง ไม่ใช่พังทีเดียว
- เครื่องที่ตั้ง `Asia/Bangkok` แดงได้แม้ container สดใหม่ ถ้ารันช่วง 00:00-06:59 น. เพราะวันที่ฝั่ง host
  นำหน้าวันที่ฝั่ง container อยู่ 1 วัน
- โปรเจกต์มี `IClock` อยู่แล้ว (`src/BuildingBlocks/BuildingBlocks.Application/IClock.cs:4-7`, `UtcNow`)
  แต่เป็นของฝั่ง application เท่านั้น — sim SP กับชุดเทสต์นี้ไม่ได้ผ่านมันเลย
- คอมเมนต์หัวไฟล์ `SpDocumentContractTests.cs:19` เขียนว่า "stable on any run day" ซึ่งไม่จริงแล้ว
  ต้องแก้พร้อมกัน ไม่งั้นคนอ่านรอบหน้าจะเชื่อผิดซ้ำ

## จุดตัดสินใจที่รอ approve

### D1: anchor ของ "วันนี้" อยู่ที่ไหน

| ทางเลือก | กลไก | ได้ | เสีย |
|---|---|---|---|
| A. Refresh | รัน seed ของ sim ใหม่อัตโนมัติก่อน integration suite เพื่อให้ anchor = วันนี้เสมอ | SP ยังใช้ `GETDATE()` จริง — fidelity กับ upstream ตัวจริงคงเดิม, assertion ทุกตัวยังเป็นค่าเป๊ะ | ต้องมีคนรับผิดชอบรัน (ดู D3), seed สร้าง LOGIN ด้วยจึงต้องส่ง password ให้ถูก, suite ช้าลง |
| B. Freeze | เพิ่มที่เก็บ anchor (เช่น `dbo.SimAnchor`) ที่ seed เขียนตอน bootstrap แล้ว SP อ่านค่านั้นแทน `GETDATE()` | deterministic 100%, ไม่ต้อง re-seed เลย, แก้จุดเดียว | SP เลิกสะท้อน upstream ตัวจริงที่ใช้ "วันนี้" จริง — ขัดเจตนาที่ประกาศไว้ที่ `SpDocumentContractTests.cs:10-13`; พฤติกรรม window เลื่อนตามเวลาจะไม่มี test คุ้มครองอีก |
| C. Test-side only | test เลิกใช้ `DateTime.Today` แล้วอ่าน anchor จากข้อมูล sim จริงแทน | diff เล็กสุด แตะฝั่ง test อย่างเดียว ปิดขา timezone ได้ทันที | **ไม่ปิดขา container-age** — `TotalRows` 42 ยังหลุดในวันถัดไป ใช้เดี่ยว ๆ ไม่พอ ต้องคู่กับ A |

### D2: หน่วยเวลาที่ทุกฝั่งใช้ร่วมกัน

| ทางเลือก | ได้ | เสีย |
|---|---|---|
| A. UTC ทุกฝั่ง | ตรงกับสภาพจริงวันนี้ (container `TZ=UTC`, CI runner UTC) และ `seed-demo.sql` ใช้ `SYSUTCDATETIME()`/`GETUTCDATE()` อยู่แล้ว (`:302`, `:317`) — แก้แค่ฝั่ง test | ต่างจาก upstream ตัวจริงที่น่าจะเดินตามเวลาไทย วันตัดรอบของ window จึงไม่ตรงความเป็นจริงทางธุรกิจ |
| B. pin `TZ=Asia/Bangkok` ให้ container ทั้ง compose และ CI | เลียน upstream จริง อ่านผลแล้วตรงสัญชาตญาณคนไทย | ต้องแก้ 3 ที่ (`docker-compose.yml`, `.github/workflows/ci.yml`, `.gitlab-ci.yml`) และ `seed-demo.sql` ยังเป็น UTC — จะมีสองหน่วยในระบบเดียว ต้องประกาศเส้นแบ่งให้ชัด |

### D3: ใครเป็นคนทำให้ anchor สดใหม่ (ตอบเมื่อ D1 = A เท่านั้น)

| ทางเลือก | ได้ | เสีย |
|---|---|---|
| A. xUnit fixture ในชุดเทสต์ | fail-safe — ใครรัน `dotnet test` ก็ได้ผลถูก, มี pattern เดิมให้ลอก (`SeedDemoIntegrationTests.RunSeedAsync` อ่านไฟล์ `.sql` แล้วแตก batch ตาม `GO`) | test ต้องถือ `sa` บน sim และต้องแทน `$(HIPPO_APP_PASSWORD)`/`$(MAMMOTH_APP_PASSWORD)` เอง, re-seed ทุก run |
| B. script wrapper ใน `scripts/` | logic อยู่ที่เดียวกับที่ CI ทำอยู่แล้ว, test ไม่ต้องถือ `sa` | ใครรัน `dotnet test` ตรง ๆ ก็ยังแดงเหมือนเดิม — ไม่ fail-safe และขัด REQ-4.4 |
| C. guard ที่ THROW เมื่อ anchor ไม่ใช่วันนี้ | แดงพร้อมเหตุผลอ่านรู้เรื่องทันที ตรงกับ REQ-1.4 — ไม่ต้องเสียเวลา re-diagnose แบบที่ผ่านมา 5 รอบ | ไม่ได้ทำให้เขียว แค่เปลี่ยนอาการ ต้องใช้คู่กับ A หรือ B เสมอ |
