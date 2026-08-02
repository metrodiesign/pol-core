# Handoff log — external-sim-documentno-format

> Rolling handoff between orchestrated teammates. Each teammate appends ONE section
> below when their task is done (or when blocked), newest at the bottom. Read every
> prior section before starting your task.

## How to use this file

- Read this whole file (plus requirements.md / design.md / tasks.md) before starting.
- When your task is done, append a new `## Task N — <name> — done by <your name>` section:
  state what changed (files), exact commands run + observed output, any deviation from
  design.md and why, and anything the next teammate needs to know.
- Do NOT edit a previous teammate's section. Append only.

## Task 1 — hippodb — done by teammate-hippodb

**ไฟล์ที่แก้:** `docker/bootstrap/02-external-sim.sql` (เฉพาะบล็อก hippodb, บรรทัด ~57-580 หลังแก้)
`.ai/specs/external-sim-documentno-format/tasks.md` (flip `[x]` + Evidence ของ task 1)

**สิ่งที่ทำ:**
- Axis-row `INSERT ... VALUES` (14 แถว): เปลี่ยน column `DocumentNo` -> `PolicySequenceNo` literal
  ตาม marker scheme ของ design.md (แถว 1-9 prefix `91`, แถว 10-14 prefix `80`, zero-pad ตามความ
  กว้างของแถวนั้นเอง — 6 หลัก VMI, 7 หลัก CMI)
- Generated-row `INSERT ... SELECT`: เปลี่ยน `DocumentNo` expression -> `PolicySequenceNo`
  expression (`RIGHT(REPLICATE('0',7) + CONVERT(varchar(7), 100+g.value), CASE ...)`, duplicate
  `SourceSystem` CASE inline ตาม design.md เพราะ T-SQL อ้าง sibling SELECT-list alias ไม่ได้)
- ลบ `CROSS APPLY` เดิมที่ parse `v.Seq` จาก `DocumentNo` ทิ้งทั้งก้อน
- เพิ่ม `CROSS APPLY` ใหม่ 2 ตัวใน shared `UPDATE`: `rb` (คำนวณ `ReferenceBranch` จาก
  `d.SaleCode` — ย้ายมาจาก inline CASE เดิมของ `ReferenceBranch`, เพราะ SET clause อื่น
  ในก้อนเดียวกันต้องใช้ค่านี้ด้วยและ T-SQL SET เห็นค่ากันเองไม่ได้) และ `ab` (คำนวณ
  `Abbrev(SourceSystem, DocumentType)` — hippodb เป็น Motor-only จึงใส่แค่ครึ่ง Motor ของตาราง
  design.md: POLICY/RENEWAL -> `กธ`, APPLICATION -> `รย`, ENDORSEMENT -> `อท`)
- `DocumentNo`/`PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber`
  compose จาก `d.PolicySequenceNo`/`rb.ReferenceBranch`/`ab.Abbrev`/literal `'69'` ตามสูตร
  design.md ทั้งหมด (ไม่มีการ parse `DocumentNo` อีกต่อไป — REQ-5.3)
- `m` CROSS APPLY (คำนวณ `CommissionPercent` tier) ที่เดิมใช้ `v.Seq % 3` เปลี่ยนเป็น
  `CAST(d.PolicySequenceNo AS int) % 3` (จำเป็นเพราะ `v` ถูกลบ — เป็น premium-derivation
  out-of-scope ตาม REQ-5.3 แต่ต้องแก้เพราะ source เดิมหายไป ไม่กระทบค่า/พฤติกรรมเชิงตรรกะ)
- Self-check: `DocumentNo NOT LIKE '77%'` -> `NOT LIKE '69%'`, ข้อความ error แก้ให้ตรง
  ("must always start with 69 (PolicyYear literal)") — ตัด reference ถึง "mammothdb owns 88"
  ออกเพราะ task 2 จะเปลี่ยนเป็น `26` (ไม่อยาก assert ล่วงหน้าแทน task 2)
- Thai round-trip self-check (`DocumentNo LIKE N'%กธ%'`) **ไม่แตะ** ตามสั่ง — ยัง PASS จริง
  เพราะ row 1 (ShowName มี "มงคล", DocumentType=POLICY) ยังได้ Abbrev `กธ` เหมือนเดิม

**สิ่งที่ไม่แตะ (ตั้งใจ, เป็น scope ของ task 2):** ไม่ได้แก้ comment "Every DocumentNo starts
'77' here and '88' in mammothdb (M9)" ที่อยู่เหนือ `DELETE FROM dbo.Documents;` ของ hippodb
(บรรทัด ~308 เดิม / ตอนนี้ตำแหน่งขยับเพราะเพิ่ม comment ใหม่หลายจุด) และ comment เหนือ
`UX_Documents_DocumentNo` index (บรรทัด ~97-99 เดิม) — design.md ระบุ 3 ช่วงบรรทัดนี้รวมกันเป็น
"file-header comments" ที่มอบให้ task 2 ทำทั้งหมด (tasks.md task 2 พูดชัดว่า "rewrite the file's
shared header comment block... the M9 prefix-disjointness note") แม้ 2 ใน 3 จุดจะอยู่ทาง
กายภาพในบล็อก hippodb ก็ตาม — **teammate task 2: อย่าลืม 2 comment นี้ยังพูดถึง '77'/'88' อยู่**

**ไม่มี deviation จาก design.md ที่มีนัยสำคัญ** — ทุกสูตรตรงตาม "Data Models & Interfaces" และ
marker scheme ตรงตาม "A discovered conflict this design resolves" 100%

**Verify (รันจริง):**
```
docker compose up pol-db-init
```
Output: `02-external-sim: hippodb OK (200 documents, 42 in the default search window).` /
`02-external-sim: mammothdb OK (200 documents, 39 in the default search window).` /
`02-external-sim: OK.` — exit code 0, ไม่มี THROW (mammothdb ยังผ่านด้วยโค้ดเดิมของมันเอง
เพราะยังไม่ถูกแตะ)

Live query ผ่าน `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` ยืนยัน: `BadPrefixCount=0`,
`DupDocumentNoCount=0`, `TotalRows=200`; pairing invariant ทั้ง 4 ชุด (SaleCode→SaleFullName,
SaleCode→BrokerCode/BrokerName, ReferenceBranch↔PolicyBranch, BrokerCode→ReferenceBranch)
คืน 0 แถวที่ละเมิด

**ตัวเลข running number จริงของ 14 axis rows (สำหรับ task 3 ไปคำนวณ test literal ใหม่)** —
เรียงตามลำดับที่ปรากฏใน `INSERT ... VALUES` (แถว 1-14), อ่านจาก live DB จริง:

| # | SourceSystem | DocumentType | SaleCode | PolicySequenceNo | DocumentNo | PolicyNumber | ApplicationNumber | PreviousPolicyNumber | EndorsementNumber |
|---|---|---|---|---|---|---|---|---|---|
| 1 | VMI | POLICY | 77001 | `910001` | `69900/กธ/910001` | `77001-69900/910001` | NULL | NULL | NULL |
| 2 | CMI | POLICY | 77001 | `9100002` | `69900/กธ/9100002` | `77001-69900/9100002` | NULL | NULL | NULL |
| 3 | VMI | POLICY | 77001 | `910003` | `69900/กธ/910003` | `77001-69900/910003` | NULL | NULL | NULL |
| 4 | VMI | RENEWAL | 77001 | `910004` | `69900/กธ/910004` | `77001-69900/910004` | NULL | `77001-68900/910003` | NULL |
| 5 | CMI | RENEWAL | 77001 | `9100005` | `69900/กธ/9100005` | `77001-69900/9100005` | NULL | `77001-68900/9100004` | NULL |
| 6 | VMI | RENEWAL | 77001 | `910006` | `69900/กธ/910006` | `77001-69900/910006` | NULL | `77001-68900/910005` | NULL |
| 7 | VMI | POLICY | 77001 | `910007` | `69900/กธ/910007` | `77001-69900/910007` | NULL | NULL | NULL |
| 8 | CMI | ENDORSEMENT | 77001 | `9100008` | `69900/อท/91000081` | `77001-69900/9100008` | NULL | `77001-68900/9100007` | `E9100008` |
| 9 | VMI | APPLICATION | 77001 | `910009` | `69900/รย/910009` | NULL | `77001-69900/910009` | NULL | NULL |
| 10 | VMI | POLICY | **90001** | `800010` | `69901/กธ/800010` | `90001-69901/800010` | NULL | NULL | NULL |
| 11 | CMI | POLICY | 77001 | `8000011` | `69900/กธ/8000011` | `77001-69900/8000011` | NULL | NULL | NULL |
| 12 | VMI | POLICY | 77001 | `800012` | `69900/กธ/800012` | `77001-69900/800012` | NULL | NULL | NULL |
| 13 | CMI | POLICY | 77001 | `8000013` | `69900/กธ/8000013` | `77001-69900/8000013` | NULL | NULL | NULL |
| 14 | VMI | ENDORSEMENT | 77001 | `800014` | `69900/อท/8000141` | `77001-69900/800014` | NULL | `77001-68900/800013` | `E800014` |

หมายเหตุแถว 10: ใช้ SaleCode `90001` (foreign-SaleCode probe) ซึ่ง `ReferenceBranch` resolve
เป็น `901` (ไม่ใช่ `900`) — ทำให้ `DocumentNo`/`PolicyNumber` ของแถวนี้ขึ้นต้น `69901` ไม่ใช่
`69900` เหมือนแถวอื่น — เป็นไปตามสูตร design.md ที่ผูก `ReferenceBranch` กับ `SaleCode` จริง
(ไม่ hardcode branch เหมือนโค้ดเก่า)

Generated-row sample ที่ตรวจแล้ว (เพื่อ sanity เท่านั้น ไม่ใช่ pinned literal เพราะ generated
rows ไม่มี test สม่ำเสมอ): g.value=40 (VMI) -> `PolicySequenceNo='000140'`; g.value=159 (CMI,
ENDORSEMENT เพราะ 159%3=0) -> `PolicySequenceNo='0000259'`, `DocumentNo='69900/อท/00002591'`
(trailing `1` ตาม REQ-1.3 ถูกต้อง)

**สิ่งที่ teammate task 2 (mammothdb) ควรรู้:**
- Pattern การใช้ CROSS APPLY แทน inline CASE ที่ SET clause อื่นต้องใช้ร่วม (เช่น
  `ReferenceBranch`) ใช้ซ้ำได้กับ mammothdb เลย — SaleCode/BrokerCode ของฝั่ง mammothdb
  ต่างชุด แต่โครงสร้างเดียวกัน
- mammothdb ไม่มีปัญหา width-split (ไม่มี CMI) ตาม design.md เลยไม่ต้องทำ marker scheme —
  axis 1-10 ใช้ index ตรงๆ พอ
- ระวัง `PreviousPolicyNumber`'s Seq-1 ต้อง cast/subtract/re-pad ตาม `RunningWidth` ของแถวนั้น
  (mammothdb ทุกแถวกว้าง 6 หลักเท่ากันหมด เลยง่ายกว่า hippodb ตรงนี้)
- DocumentNo ฝั่ง mammothdb เป็น `'1-' + base` สำหรับ ENDORSEMENT (คนละ position จาก Motor's
  trailing `'1'`) — อย่าสลับ
- Self-check message เดิม parenthetical เกี่ยวกับ prefix อีกฝั่ง (เช่น "hippodb owns 69")
  แนะนำให้ตัดออกเหมือนที่ทำในฝั่งนี้ เพื่อไม่ต้องผูก 2 self-check เข้าด้วยกัน

## Task 2 — mammothdb — done by teammate-mammothdb

**ไฟล์ที่แก้:** `docker/bootstrap/02-external-sim.sql` (บล็อก mammothdb ทั้งหมด + 3 จุด header
comment ที่ physically อยู่ในบล็อก hippodb/ต้นไฟล์ตามที่ task 1 ระบุให้เป็น scope ของ task นี้)
`.ai/specs/external-sim-documentno-format/tasks.md` (flip `[x]` + Evidence ของ task 2)

**สิ่งที่ทำ:**
- Header comments (3 จุด, ทั้งหมดอยู่นอกบล็อก mammothdb ทางกายภาพตามที่ task 1 ส่งมอบ):
  - บรรทัดต้นไฟล์: เพิ่ม reference line ใหม่ชี้ไป `.ai/specs/external-sim-documentno-format/
    {requirements,design}.md` ต่อจาก reference เดิมของ `products-sp-gateway`
  - Comment เหนือ `UX_Documents_DocumentNo` index (hippodb block): เปลี่ยนจาก "disjoint prefixes
    ('77…'/'88…')" เป็น "disjoint PolicyYear literals ('69…'/'26…')" พร้อม reference REQ-6.1
  - Comment เหนือ `DELETE FROM dbo.Documents;` ของ hippodb: เปลี่ยนจาก "starts '77'/'88'" เป็น
    "starts with PolicyYear '69'/'26'"
- Axis-row `INSERT ... VALUES` (10 แถว): เปลี่ยน column `DocumentNo` -> `PolicySequenceNo` literal
  index ตรงๆ 1-10 zero-pad 6 หลัก (ไม่มี marker scheme เพราะ mammothdb ไม่มี CMI ไม่มีปัญหา
  width-split) เพิ่ม comment อธิบายเหตุผล (ตาม design.md "A discovered conflict...")
- Generated-row `INSERT ... SELECT`: เปลี่ยน `DocumentNo` expression -> `PolicySequenceNo`
  expression แบบง่ายกว่า hippodb — `RIGHT('000000' + CONVERT(varchar(6), 100 + g.value), 6)`
  (ไม่มี CASE แตกกว้างเพราะ mammothdb ไม่มี CMI เลย ไม่ต้อง duplicate SourceSystem CASE เหมือน
  hippodb); แก้ comment ที่อ้างถึง "DocumentNo abbreviation two lines down" (ของเดิมอ้างถึง
  inline abbreviation ที่ถูกลบไปแล้ว) ให้ตรงกับโครงสร้างใหม่ที่ Abbrev คำนวณใน UPDATE แทน
- ลบ `CROSS APPLY` เดิมที่ parse `v.Seq` จาก `DocumentNo` ทิ้งทั้งก้อน (`CAST(REPLACE(RIGHT(...),
  '-10', '') AS int)`)
- เพิ่ม `CROSS APPLY` ใหม่ 2 ตัวใน shared `UPDATE` (pattern เดียวกับ hippodb ตามที่ task 1 แนะนำ):
  `rb` (คำนวณ `ReferenceBranch` จาก `d.SaleCode` — ย้ายมาจาก inline CASE เดิมของ
  `ReferenceBranch`, เพราะ SET clause อื่นในก้อนเดียวกันต้องใช้ค่านี้ด้วย) และ `ab` (คำนวณ
  `Abbrev(SourceSystem, DocumentType)` — mammothdb เป็น Non-Motor-only จึงใส่แค่ครึ่ง Non-Motor
  ของตาราง design.md: POLICY/RENEWAL -> `POL`, APPLICATION -> `APP`, ENDORSEMENT -> `END`)
- `DocumentNo`/`PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber`
  compose จาก `d.PolicySequenceNo`/`rb.ReferenceBranch`/`ab.Abbrev`/literal `'26'` ตามสูตร
  design.md ทั้งหมด — `DocumentNo` ของ ENDORSEMENT ใช้ `'1-' + base` (prefix ก่อนทั้งก้อน, REQ-1.2)
  คนละตำแหน่งจาก Motor's trailing `'1'` (REQ-1.3) (ไม่มีการ parse `DocumentNo` อีกต่อไป — REQ-5.3)
- **แก้ bug แฝงของโค้ดเดิม**: `PolicyNumber`/`ApplicationNumber`/`PreviousPolicyNumber` เดิม
  hardcode branch เป็น `'-69900/'`/`'-68900/'` เสมอ ไม่ใช้ `ReferenceBranch` ที่คำนวณจริงต่อ
  `SaleCode` (ทั้งที่ `ReferenceBranch` เองก็คำนวณต่างกันตาม SaleCode อยู่แล้วในบล็อกเดียวกัน) —
  ตอนนี้ทั้ง 3 ฟิลด์ผูกกับ `rb.ReferenceBranch` จริงตามแถว ตรงตามสูตร design.md's Data Models
  section (`SaleCode + '-' + PolicyYear + ReferenceBranch + '/' + ...`) และ REQ-9.1 — สังเกตได้
  จาก axis row 1 (SaleCode `90001`) ที่ตอนนี้ `PolicyNumber` ขึ้น `26901` (ไม่ใช่ `26900`)
- `m` CROSS APPLY (คำนวณ `CommissionPercent` tier) ที่เดิมใช้ `v.Seq % 3` เปลี่ยนเป็น
  `CAST(d.PolicySequenceNo AS int) % 3` (จำเป็นเพราะ `v` ถูกลบ ไม่กระทบค่า/พฤติกรรมเชิงตรรกะ
  เหมือนที่ task 1 ทำกับ hippodb)
- Self-check: `DocumentNo NOT LIKE '88%'` -> `DocumentNo NOT LIKE '26%' AND DocumentNo NOT LIKE
  '1-26%'` (ครอบทั้ง REQ-1.1's plain form และ REQ-1.2's endorsement `'1-'` prefix); ตัด
  parenthetical "(hippodb owns 77)" ออกตามคำแนะนำใน HANDOFF ของ task 1
- Thai round-trip self-check: `DocumentNo LIKE N'%อค%'` -> `PolicyBranch LIKE N'%สาขา%'` (REQ-6.5,
  เพราะ mammothdb `DocumentNo` กลายเป็น ASCII-only ล้วนหลังเปลี่ยน Abbrev เป็น `POL`/`APP`/`END`)
- แก้ comment ตกค้าง 1 จุดที่พบระหว่างทำ (ไม่ได้อยู่ใน 3 จุดที่ task 1 ระบุไว้ แต่จำเป็นเพราะ
  reference ตัวแปรที่ถูกลบไปแล้ว): comment เหนือ `SaleFullName` CASE เดิมอ้างถึง "not v.Seq" —
  แก้เป็น "not the running number" เพราะ `v` ไม่มีอยู่แล้ว

**ไม่มี deviation จาก design.md ที่มีนัยสำคัญ** — ทุกสูตรตรงตาม "Data Models & Interfaces" 100%;
การลดความซับซ้อนของ generated-row `PolicySequenceNo` expression (ไม่มี CASE แตกกว้างเหมือน
hippodb) เป็นการ simplify ที่สอดคล้องกับ design.md เอง เพราะ mammothdb ไม่เคยมี `SourceSystem =
'CMI'` — ผลลัพธ์ตัวเลขเหมือนกับถ้าใช้สูตร generic ของ design.md แล้ว CASE ตกไปที่ branch `ELSE 6`
เสมอ ไม่ใช่การเปลี่ยนพฤติกรรม

**Verify (รันจริง):**
```
docker compose up pol-db-init
```
Output: `02-external-sim: hippodb OK (200 documents, 42 in the default search window).` /
`02-external-sim: mammothdb OK (200 documents, 39 in the default search window).` /
`02-external-sim: OK.` — exit code 0, ไม่มี THROW

Live query ผ่าน `docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` ยืนยัน (ฝั่ง mammothdb):
`BadPrefixCount=0`, `DupDocumentNoCount=0`, `TotalRows=200`, `CrossCatalogDup=0` (join
hippodb.dbo.Documents กับ mammothdb.dbo.Documents บน `DocumentNo` ไม่มีแถวชนกัน); pairing
invariant ทั้ง 4 ชุด (SaleCode→SaleFullName, SaleCode→BrokerCode, ReferenceBranch↔PolicyBranch,
BrokerCode→ReferenceBranch) คืน 0 แถวที่ละเมิด

**ตัวเลข running number จริงของ 10 axis rows (สำหรับ task 3 ไปคำนวณ test literal ใหม่)** —
เรียงตามลำดับที่ปรากฏใน `INSERT ... VALUES` (แถว 1-10), อ่านจาก live DB จริง:

| # | SourceSystem | DocumentType | SaleCode | ReferenceBranch | PolicySequenceNo | DocumentNo | PolicyNumber | ApplicationNumber | PreviousPolicyNumber | EndorsementNumber |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | FIRE | POLICY | 90001 | 901 | `000001` | `26901/POL/000001` | `90001-26901/000001` | NULL | NULL | NULL |
| 2 | MISC | POLICY | 90001 | 901 | `000002` | `26901/POL/000002` | `90001-26901/000002` | NULL | NULL | NULL |
| 3 | FIRE | POLICY | 90001 | 901 | `000003` | `26901/POL/000003` | `90001-26901/000003` | NULL | NULL | NULL |
| 4 | FIRE | RENEWAL | 90001 | 901 | `000004` | `26901/POL/000004` | `90001-26901/000004` | NULL | `90001-25901/000003` | NULL |
| 5 | MISC | RENEWAL | 90001 | 901 | `000005` | `26901/POL/000005` | `90001-26901/000005` | NULL | `90001-25901/000004` | NULL |
| 6 | MISC | APPLICATION | 90001 | 901 | `000006` | `26901/APP/000006` | NULL | `90001-26901/000006` | NULL | NULL |
| 7 | FIRE | ENDORSEMENT | 90001 | 901 | `000007` | `1-26901/END/000007` | `90001-26901/000007` | NULL | `90001-25901/000006` | `E000007` |
| 8 | MISC | POLICY | 90001 | 901 | `000008` | `26901/POL/000008` | `90001-26901/000008` | NULL | NULL | NULL |
| 9 | FIRE | POLICY | **77001** | **900** | `000009` | `26900/POL/000009` | `77001-26900/000009` | NULL | NULL | NULL |
| 10 | MISC | POLICY | 90001 | 901 | `000010` | `26901/POL/000010` | `90001-26901/000010` | NULL | NULL | NULL |

หมายเหตุแถว 9: ใช้ SaleCode `77001` (foreign-SaleCode probe, สลับด้านกับ row 10 ของ hippodb) ซึ่ง
`ReferenceBranch` resolve เป็น `900` (ตรงกับ hippodb's own default branch สำหรับ `77001`) —
`DocumentNo`/`PolicyNumber` ของแถวนี้ขึ้นต้น `26900` ไม่ใช่ `26901` เหมือนแถวอื่น เป็นไปตามสูตร
design.md ที่ผูก `ReferenceBranch` กับ `SaleCode` จริง

Generated-row sample ที่ตรวจแล้ว (เพื่อ sanity เท่านั้น ไม่ใช่ pinned literal): g.value=140 (POLICY)
-> `PolicySequenceNo='000140'`, `DocumentNo='26901/POL/000140'`; g.value=220 (ENDORSEMENT เพราะ
220%3≠0 จริง ๆ แต่ตัวอย่างที่ query ได้คือ DocumentId ที่ map กับ g.value ซึ่งมี DocumentType=
ENDORSEMENT) -> `PolicySequenceNo='000220'`, `DocumentNo='1-26901/END/000220'` (`'1-'` prefix ตาม
REQ-1.2 ถูกต้อง, คนละตำแหน่งจาก hippodb's trailing `'1'`)

**สิ่งที่ teammate task 3 (Integration.Tests) ควรรู้:**
- ตารางด้านบนคือค่าจริงจาก live DB สำหรับ 10 axis rows ของ mammothdb — ใช้แทนค่า `DocumentNo`
  แบบเก่า (`88001-69900/...`) ทั้งหมดใน `SpDocumentContractTests.cs` / `SpDocumentGatewayIntegrationTests.cs`
- mammothdb's `SeqPrefix`/marker scheme **ไม่เปลี่ยนรูปแบบ** จาก pattern เดิม (ยังเป็น index
  1-10 ตรงๆ 6-digit) ต่างจาก hippodb ที่เปลี่ยนเป็น marker-prefixed scheme — tests ฝั่ง mammothdb
  จึงแก้แค่ค่าตัวเลข ไม่ต้องแก้โครงสร้าง marker
- `DocumentNo` ฝั่ง mammothdb เป็น ASCII-only ล้วนแล้ว (`POL`/`APP`/`END`) — ถ้า test ไหนยัง
  smart-search ด้วย substring ที่เคยเป็น Thai (`อค`/`บต`) ต้องเปลี่ยนเป็น `POL`/`END` แทน
  (`APP` ไม่ปรากฏใน axis rows ที่เป็น POLICY/RENEWAL)
- `Motor_last_page_is_ordered_by_the_thai_letter_in_the_document_number` เป็น test เฉพาะฝั่ง
  hippodb (Motor) — ไม่มี counterpart ฝั่ง mammothdb เพราะ mammothdb ไม่มี Thai abbreviation ใน
  `DocumentNo` เลย (ordering test ฝั่งนี้ ถ้ามี น่าจะต้องอิง `PolicyBranch`/`ShowName` แทน แต่
  design.md/tasks.md ไม่ได้ระบุ replaced-test สำหรับฝั่งนี้ — น่าจะไม่ต้องแก้อะไรเพิ่ม)
- `PreviousPolicyNumber`'s Seq-1 ฝั่ง mammothdb ง่ายกว่า hippodb เพราะทุกแถวกว้าง 6 หลักเท่ากันหมด
  (ไม่มี CMI 7-digit) — ไม่ต้องคำนวณ width แยกตาม SourceSystem เหมือน hippodb

## Task 3 — Integration.Tests — done by teammate-tests

**ไฟล์ที่แก้:** `tests/Integration.Tests/SpDocumentContractTests.cs`,
`tests/Integration.Tests/SpDocumentGatewayIntegrationTests.cs`, `docs/reference/products.md` (~บรรทัด
160), `.ai/specs/external-sim-documentno-format/tasks.md` (flip `[x]` + Evidence ของ task 3)

**ก่อนเริ่ม re-pin ใดๆ**: query live DB จริง (ไม่เชื่อ HANDOFF เฉยๆ) — `docker compose up
pol-db-init` แล้ว query ทุก axis row ทั้ง 14 (hippodb) + 10 (mammothdb) ผ่าน
`docker exec pol-db /opt/mssql-tools18/bin/sqlcmd` ตรงกับตาราง HANDOFF ของ task 1/2 100% ทุกแถว —
ยืนยันว่าไม่มี drift ระหว่าง task 1/2 เสร็จกับตอนที่ task 3 เริ่ม

**สิ่งที่ทำ:**
- `Seqs()` ใน `SpDocumentContractTests.cs`: เขียนใหม่ให้อ่าน `SourceSystem`/`DocumentType` จาก row
  dictionary ที่ query มาแล้ว แล้ว strip ตัว `'1'` ท้าย tail เฉพาะเมื่อ
  `SourceSystem IN ('CMI','VMI') AND DocumentType = 'ENDORSEMENT'` — ไม่ใช้ string-length heuristic
  เลย ตาม design.md's ambiguity note (`DocumentNumbers()` ไม่ต้องแก้ เพราะ raw DocumentNo ไม่เคย
  ต้อง strip อะไร — เฉพาะ `Seqs()` เท่านั้นที่ทำงานนี้)
- Re-pin `Side` records ทั้งสองไฟล์ทั้งหมดจากค่าที่ query จริง (ไม่ hand-derive): `MotorSide.SeqPrefix`
  `"91"`, `SeqPrefixHits ["910001","9100002","910004","910009"]` (แถว 1/2/4/9),
  `RenewalDroppedSeqs ["9100005","910006"]` (แถว 5/6), `RenewalKeptSeq "910004"` (แถว 4),
  `ApplicationSeq "910009"` (แถว 9), `PolicySeq "910001"` (แถว 1),
  `LikeMetacharacterSeq "8000011"` (แถว 11), `PaidSeqs ["910007","9100008"]` (แถว 7/8),
  `ForeignSaleCodeSeq "800010"` (แถว 10); `NonMotorSide.SeqPrefix "00000"` (ไม่ใช่ `"96000"` เดิม —
  เพราะ scheme ใหม่เป็น index ตรง 1-10 ไม่มี marker, "00000" เกิดขึ้นเองจาก zero-pad 6 หลักของเลข
  1-9 หลักเดียว ต่างจากแถว 10 ที่เป็น "000010"), `SeqPrefixHits
  ["000001","000002","000004","000006"]`, `RenewalDroppedSeqs ["000005"]`,
  `RenewalKeptSeq "000004"`, `ApplicationSeq "000006"`, `PolicySeq "000001"`,
  `LikeMetacharacterSeq "000010"`, `PaidSeqs ["000007","000008"]`, `ForeignSaleCodeSeq "000009"`.
  ทุกค่า verify ด้วยการ emulate SP predicate จริงผ่าน raw SQL query ต่อ live DB (ไม่ใช่แค่อ่านจาก
  axis table เฉยๆ) — โดยเฉพาะ `SeqPrefixHits`/`SeqPrefix` ที่ query จำลอง
  `@SearchText LIKE '%91%'`/`'%00000%'` จริงเพื่อยืนยันไม่มี generated row ไหนหลุดเข้ามาปน (เช่น
  g.value=91 บน hippodb จะได้ PolicySequenceNo `"000191"` ซึ่งมี substring `"91"` ด้วย — ต้อง verify
  ว่าแถวนั้นไม่ได้อยู่ใน default 42-row window ของ SaleCode 77001/UNPAID จริง ไม่ใช่แค่คิดว่าไม่ชน)
- **พบ + แก้ bug เดิมที่ค้างมาก่อน task นี้**: `Policy_number_and_application_number_match_exactly`
  hardcode literal `"69900"` ตรงๆ สำหรับทั้ง 2 sides (`$"{side.SaleCode}-69900/{side.PolicySeq}"`) —
  เคยใช้ได้เพราะโค้ด SQL เดิม (ก่อน task 2 แก้) hardcode branch ของ `PolicyNumber` เป็น `"69900"`
  เสมอไม่ว่า `ReferenceBranch` จริงจะเป็นอะไร (bug ที่ task 2's HANDOFF บันทึกไว้แล้วว่าแก้). พอ task 2
  ผูก `PolicyNumber` เข้ากับ `rb.ReferenceBranch` จริง ค่า literal นี้ผิดทันทีสำหรับ NonMotorSide
  (SaleCode `90001` → ReferenceBranch จริง `901` → ต้องเป็น `"26901"` ไม่ใช่ `"69900"`) — เพิ่ม field
  ใหม่ `Side.PolicyYearBranch` (`"69900"` Motor / `"26901"` NonMotor) แทน literal ตรงๆ
  เดียวกันนี้พบซ้ำใน `SpDocumentGatewayIntegrationTests.cs`: `Every_column_of_a_row_lands_in_its_own_field`
  hardcode `Assert.Equal("69", item.PolicyYear)`/`Assert.Equal("69", item.ReferenceYear)` สำหรับทั้ง
  2 sides — เพิ่ม field `Side.PolicyYear` (`"69"`/`"26"`) แทน
- `AxisDocumentNo`/`AxisPolicyNumber`/`PaidPolicyNumber` ใน `SpDocumentGatewayIntegrationTests.cs`
  re-pin จาก axis row 1 (ทั้งสอง sides) และแถว PAID (แถว 7 ทั้งสอง sides): MotorSide
  `AxisPolicyNumber "77001-69900/910001"`, `AxisDocumentNo "69900/กธ/910001"`,
  `PaidPolicyNumber "77001-69900/910007"`; NonMotorSide
  `AxisPolicyNumber "90001-26901/000001"`, `AxisDocumentNo "26901/POL/000001"`,
  `PaidPolicyNumber "90001-26901/000007"` — สังเกตว่า `AxisDocumentNo` ใหม่ไม่มี `SaleCode` prefix
  แล้ว (REQ-1.1) ต่างจากรูปแบบเดิม `"{SaleCode}-{Year}{Branch}/{Abbrev}/{Seq}"`
- แทนที่ `Motor_last_page_is_ordered_by_the_thai_letter_in_the_document_number` ด้วย
  `Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter` ตามที่ design.md's Testing
  Strategy ระบุ: เพิ่ม helper `AllPagesAsync(side)` (reuse โดย `The_pages_cut_one_document_number_
  ordered_list` เดิมด้วย) walk ทุกหน้า แล้ว assert ทุก position ของ `DocumentNo` ที่มี `"อท"` (ตรวจ
  ด้วย `Contains`) ต้องมากกว่าทุก position ที่ไม่มี — verify empirically ก่อนเขียน assertion ด้วย raw
  query ต่อ live DB: `firstEndorsementPos=31 > lastNonEndorsementPos=30` บน default 42-row Motor set
  จริง (ไม่ hand-derive ว่า property นี้จะ hold)
- `docs/reference/products.md` บรรทัด 160: เปลี่ยน `DocumentNo` example จาก
  `90001-69900/บต/900008` (fictional format เดิม) เป็น `69900/กธ/910001` (ค่าจริงจาก axis row 1 ของ
  hippodb) — แตะเฉพาะแถวนี้ตามที่ tasks.md ระบุ ไม่แตะ `ReferencePre`/`ReferenceNo`/
  `PolicyNumber`/... ที่มี example เดิมดูเหมือนจะไม่ตรง schema จริงเช่นกัน (นอก scope ของ task 3
  ตามที่ระบุไว้ชัดว่า "~line 160" เท่านั้น — ถ้าจะแก้ควรเป็น task/spec แยก)

**Deviation จาก design.md (มี 2 จุด ทั้งสองมี root cause ชัดเจน ไม่ใช่การเดา):**

1. **`Omitting_every_optional_parameter_applies_the_documented_defaults(Motor)` — เกี่ยวกับ feature
   นี้จริง แต่ design.md ไม่ได้ระบุไว้ล่วงหน้า.** Root cause: REQ-1.1 ตัด `SaleCode` prefix ออกจาก
   `DocumentNo` ทำให้ abbreviation กลายเป็น sort key หลักทันทีหลัง `PolicyYear+ReferenceBranch`
   (แทนที่จะถูกฝังลึกกลางสตริงเหมือนเดิม) — บน default 42-row Motor set (SaleCode เดียว) หน้าแรก 25
   แถวจึงเป็น `DocumentType=POLICY` ล้วน (ยืนยันด้วย query ตรง: TOP 25 ORDER BY DocumentNo ทุกแถวคือ
   POLICY) เพราะ `กธ` (POLICY/RENEWAL) sort ก่อน `รย` (APPLICATION) sort ก่อน `อท` (ENDORSEMENT) และ
   RENEWAL 1 แถวที่มีอยู่ก็มี running number สูงพอที่จะตกไปหลัง POLICY ทั้งหมดในหน้าแรกเช่นกัน. แก้โดย
   assert ความหลากหลายของ `DocumentType`/`SourceSystem` บน "ผลลัพธ์ทั้งชุด" (เดิน `AllPagesAsync`
   ทุกหน้า) แทนหน้าแรกอย่างเดียว — คง intent เดิม (พิสูจน์ว่า `ALL` ไม่ได้ถูก filter แอบแฝง) แต่ไม่พึ่ง
   สมมติฐานเรื่องการกระจายตัวของหน้าแรกที่ format ใหม่ทำให้ไม่จริงอีกต่อไป
2. **3 tests ล้มเหลวจาก pre-existing clock-skew ไม่เกี่ยวกับ feature นี้เลย — ไม่แก้โค้ด, บันทึกเป็น
   known issue.** `Coverage_bounds_are_inclusive_on_both_ends` (ทั้ง 2 sides) กับ
   `Motor_coverage_start_window_includes_the_row_sitting_exactly_six_months_back` ใช้
   `DateTime.Today`/`.AddMonths(-6)` ตรงๆ (โค้ดส่วนนี้ task 3 ไม่ได้แตะเลย มีมาก่อน feature นี้) —
   root cause (ยืนยันด้วยเปรียบเทียบ clock ตรง 3 ทาง: `date` host, `docker exec pol-db date`,
   `SELECT GETDATE()`): host เป็น UTC+7 แต่ container `pol-db` เป็น UTC, และช่วงเวลาที่รันทดสอบ
   (00:00-07:00 เวลาไทย) ตรงกับช่วงที่ทั้งสอง clock เห็นคนละวันปฏิทิน — `DateTime.Today` (host-local)
   กับ seed's `@today` (`GETDATE()`-based, container-UTC) เลยเทียบกันคนละวัน ทำให้ query
   date-boundary ว่างเปล่า. Reproduce ซ้ำได้แน่นอน 100% ที่ moment เดียวกัน, ยืนยัน theory ด้วยการรัน
   เฉพาะ 3 test นี้ซ้ำภายใต้ `TZ=UTC` (จำลอง environment แบบที่ CI runner ซึ่งปกติเป็น UTC จะเห็น) —
   ผ่านหมดโดยไม่แตะโค้ดเลย. ไม่แก้ในรอบนี้เพราะ: (ก) ไม่มี REQ ไหนของ spec นี้ครอบคลุมเรื่อง
   timezone, (ข) การแก้จริงต้องแตะ date-handling ของ test infra ที่ใช้ร่วมกันทั้ง repo หรือ container
   TZ config ซึ่งกว้างเกิน scope ของ DocumentNo format — ควรเป็น ticket แยก. **คำแนะนำสำหรับคนถัดไปที่
   รัน Integration.Tests บนเครื่อง dev ที่ timezone ไม่ใช่ UTC**: ถ้าเจอ 3 test นี้ fail ด้วย
   `Actual: []` ให้เช็คเวลา host ก่อนสงสัยโค้ด — ลอง `TZ=UTC dotnet test ...` เพื่อแยกว่าเป็นปัญหา
   clock หรือปัญหาจริง

**Verify (รันจริง):**
```
dotnet build pol-core.slnx
```
→ `ok dotnet build: 64 projects, 0 errors, 0 warnings`

```
source .env.integration && dotnet test tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"
```
รันครั้งแรก (ก่อนแก้ deviation #1) → 4 failed (3 จาก clock-skew ข้างต้น + 1 จาก deviation #1);
root-cause ทั้งหมดก่อนแตะโค้ดใดๆ ตามที่สั่ง ("ห้ามเดา"). หลังแก้ deviation #1 แล้วรันซ้ำที่ moment
พ้นช่วง clock-skew (verbatim, ไม่ override TZ) → `Passed! - Failed: 0, Passed: 113, Skipped: 0,
Total: 113` (ยืนยันซ้ำอีกครั้งก่อนหน้าด้วย `TZ=UTC` เช่นกัน — ผลตรงกัน)

```
dotnet test pol-core.slnx
```
(รันภายใต้ `TZ=UTC` เป็น belt-and-suspenders สุดท้าย — เลี่ยง clock-skew ข้างต้นแทนที่จะรอเวลา) →
ทุกโปรเจกต์ผ่านหมด 0 failed: `BuildingBlocks.Tests 43/43`, `Carts.Tests 15/15`,
`Checkouts.Tests 13/13`, `Products.Tests 137/137`, `Orders.Tests 76/76`, `Iam.Tests 61/61`,
`Merchants.Tests 120/120`, `Payments.Tests 162/162`, `Admins.Tests 95/95`,
`Integration.Tests 113/113`, `Architecture.Tests 200/200`, `Hosts.Tests 379/379`,
`Levels.Tests 6/6`, `Positions.Tests 6/6`

**REQ traceability cross-check (Definition of Done ข้อสุดท้ายของ spec):** ไล่ REQ-1 ถึง REQ-9 (ทุก
sub-item) เทียบกับ design.md's "Requirement Traceability" table — REQ-1/2/3/4/5/6/7 ปิดโดย task 1/2
(SQL), REQ-8.1-8.4 (regression) + REQ-8.5 (replaced test) + REQ-9 ปิดโดย task 3 (tests) — **ไม่มี REQ
ไหนตกหล่น ไม่มี task/test ไหนขาด**

**สรุปสถานะ spec:** `external-sim-documentno-format` เสร็จสมบูรณ์ทั้ง 3 tasks — ไม่มีงานค้างที่เป็น
scope ของ spec นี้เอง สิ่งที่ค้างอยู่นอก scope (ไม่ block การปิด spec): (1) dev-machine/container
timezone mismatch เป็น pre-existing issue ที่ควรเปิด ticket แยกถ้าต้องการแก้จริง (ไม่ใช่ของ spec
นี้), (2) `docs/reference/products.md`'s `ReferencePre`/`ReferenceNo`/`PolicyNumber`/
`ApplicationNumber`/`PreviousPolicyNumber`/`EndorsementNumber` example values ดูเหมือนจะไม่ตรง
schema จริงเช่นกัน (สังเกตระหว่างทำ แต่ไม่แตะเพราะนอก scope ที่ tasks.md ระบุไว้ชัดว่าเฉพาะบรรทัด
DocumentNo/SaleCode) — ถ้าต้องการความถูกต้องเต็มไฟล์ ควรเป็น doc-fix task แยก
