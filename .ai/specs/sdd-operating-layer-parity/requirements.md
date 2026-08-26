# ข้อกำหนด: SDD Operating-Layer Parity

> Status: approved 2026-08-25
> Status-Note: amended 2026-08-25 — archived state ใช้ canonical directory location ร่วมกับ artifact bytes

## ภาพรวม

งานนี้ทำให้ SDD operating layer ของ `pol-core` ใช้ shared deterministic contract เดียวกันสำหรับ Claude, Codex และ OpenCode พร้อมย้าย historical specs 62 directories อย่างตรวจสอบย้อนกลับได้ โดยคง product runtime, .NET/SQL security floor, CI topology และ release flow เดิมทั้งหมด เอกสารนี้กำหนดเฉพาะพฤติกรรมที่ต้องสังเกตได้จาก `AC-1` ถึง `AC-32` และ adversarial property โดยยังไม่กำหนด architecture หรือ implementation tasks และไม่ถือว่า downstream artifact ใดได้รับ approval แล้ว

## ขอบเขต

### อยู่ในขอบเขต

- Canonical parsing และ validation ของ status, phase, task ID, Evidence, EARS, trace, slice และ derived state
- Task completion gate แบบ fail-closed พร้อม .NET default commands และ safe cache
- No-fabrication migration ของ historical specs 62 directories
- Verdict parity ของ Claude, Codex และ OpenCode ตาม runtime capability จริง
- Strict checks ใน GitHub และ GitLab เฉพาะ verify paths
- Rollback แบบแยก layer และแยก migration batch

### อยู่นอกขอบเขต

- Product runtime behavior, domain, API, database schema หรือ payment flow ใหม่
- B0, goals, runs, calibration, `spec_to_goal`, Universal PR Quality Gate และ review-fanout policy
- Historical spec archive relocation, Pi extension, dependency ใหม่, deploy หรือ release execution

## REQ-1: การรักษา product runtime และขอบเขตเดิม

**เรื่องราวผู้ใช้:** ในฐานะผู้ดูแล `pol-core` ฉันต้องการย้าย SDD operating layer โดยไม่แตะ payment platform runtime เพื่อให้การเปลี่ยน process tooling ไม่สร้าง product regression

**เกณฑ์รับงานแบบ EARS:**

- 1.1  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง bytes ใต้ `src/**` โดยไม่มีการเปลี่ยนแปลง
- 1.2  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง bytes ใต้ `tests/**` โดยไม่มีการเปลี่ยนแปลง
- 1.3  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง bytes ใต้ `docker/**` โดยไม่มีการเปลี่ยนแปลง
- 1.4  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง `pol-core.slnx` โดยไม่มีการเปลี่ยนแปลง
- 1.5  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง `Directory.Packages.props` โดยไม่มีการเปลี่ยนแปลง
- 1.6  WHILE งานนี้ถูก implement THE SYSTEM SHALL คง runtime dependency manifests โดยไม่มีการเปลี่ยนแปลง
- 1.7  WHILE GitHub CI ถูกแก้ THE SYSTEM SHALL คง command, image, service, secret topology และ semantics ของ job `dotnet`
- 1.8  WHILE GitHub CI ถูกแก้ THE SYSTEM SHALL คง command, image, service, secret topology และ semantics ของ job `docker-build`
- 1.9  WHILE GitHub CI ถูกแก้ THE SYSTEM SHALL คง command, image, service, secret topology และ semantics ของ job `dotnet-integration`
- 1.10  WHILE GitLab CI ถูกแก้ THE SYSTEM SHALL คง package jobs โดยไม่เปลี่ยน command, image, service, registry หรือ semantics
- 1.11  WHILE GitLab CI ถูกแก้ THE SYSTEM SHALL คง deploy jobs โดยไม่เปลี่ยน manual gate, SSH, SQL service หรือ release semantics
- 1.12  WHEN canonical docs ถูกตรวจ THE SYSTEM SHALL อธิบาย current .NET modules ตรงกับ filesystem
- 1.13  WHEN canonical docs ถูกตรวจ THE SYSTEM SHALL อธิบาย current runtime `DbContext` clusters ตรงกับ filesystem
- 1.14  WHEN canonical docs ถูกตรวจ THE SYSTEM SHALL อธิบาย app-layer merchant isolation ตรงกับ runtime contract ปัจจุบัน
- 1.15  WHEN canonical docs ถูกตรวจ THE SYSTEM SHALL ระบุ GitHub CI topology เป็น 4 jobs ตาม workflow ปัจจุบัน
- 1.16  WHEN canonical docs และ templates ถูกตรวจ THE SYSTEM SHALL ใช้ handoff schema เดียวกัน
- 1.17  WHEN canonical docs และ adapters ถูกตรวจ THE SYSTEM SHALL รักษา human-authorized git boundaries ตาม project policy
- 1.18  WHILE งานนี้ถูก implement THE SYSTEM SHALL ไม่เพิ่ม dependency, ภาษา หรือ runtime ใหม่

## REQ-2: Canonical artifact contract และ phase safety

**เรื่องราวผู้ใช้:** ในฐานะผู้ใช้ SDD ฉันต้องการให้ artifact ทุกชนิดถูก parse และ gate ด้วย contract เดียว เพื่อไม่ให้ syntax drift หรือ consumer ใดตีความสถานะต่างกัน

**เกณฑ์รับงานแบบ EARS:**

- 2.1  WHEN parser อ่าน artifact status THE SYSTEM SHALL ยอมรับเฉพาะ `draft`, `approved YYYY-MM-DD`, `superseded YYYY-MM-DD by` ตามด้วย feature ID ที่ถูกต้อง และ `unknown`
- 2.2  WHEN parser อ่าน artifact status THE SYSTEM SHALL นับเฉพาะ status line ที่อยู่นอก fenced code block
- 2.3  THE SYSTEM SHALL อนุญาต status line ได้หนึ่งบรรทัดต่อ phase artifact
- 2.4  IF status line หาย THEN THE SYSTEM SHALL block downstream phase
- 2.5  IF status line malformed THEN THE SYSTEM SHALL block downstream phase
- 2.6  IF artifact มี status ที่ขัดกัน THEN THE SYSTEM SHALL block downstream phase
- 2.7  IF artifact status เป็น `unknown` THEN THE SYSTEM SHALL block downstream phase
- 2.8  WHILE downstream phase ถูก gate THE SYSTEM SHALL ไม่อนุมาน approval จาก checkbox
- 2.9  WHILE downstream phase ถูก gate THE SYSTEM SHALL ไม่อนุมาน approval จาก code existence
- 2.10  WHILE downstream phase ถูก gate THE SYSTEM SHALL ไม่อนุมาน approval จาก commit message
- 2.11  WHILE downstream phase ถูก gate THE SYSTEM SHALL ไม่อนุมาน approval จาก conversation
- 2.12  WHEN Requirements-first workflow เข้า design phase THE SYSTEM SHALL require approved `requirements.md`
- 2.13  WHEN Design-first workflow เข้า requirements phase THE SYSTEM SHALL require approved `design.md`
- 2.14  WHEN feature workflow เข้า tasks phase THE SYSTEM SHALL require approved `requirements.md`
- 2.15  WHEN feature workflow เข้า tasks phase THE SYSTEM SHALL require approved `design.md`
- 2.16  WHEN feature workflow เข้า implement phase THE SYSTEM SHALL require approved `requirements.md`, `design.md` และ `tasks.md`
- 2.17  WHEN feature workflow เข้า implement phase THE SYSTEM SHALL require feature trace contract ที่ผ่าน
- 2.18  WHEN bugfix workflow เข้า tasks phase THE SYSTEM SHALL require approved `bugfix.md`
- 2.19  WHEN bugfix workflow เข้า implement phase THE SYSTEM SHALL require approved `bugfix.md` และ `tasks.md`
- 2.20  WHEN bugfix workflow เข้า implement phase THE SYSTEM SHALL require `F/B` trace contract ที่ผ่าน
- 2.21  WHEN parser อ่าน task opening THE SYSTEM SHALL รับ exact case-sensitive ID ตาม `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`
- 2.22  WHEN parser คืน task IDs THE SYSTEM SHALL รักษา byte value ของ ID เดิม
- 2.23  WHEN parser คืน task IDs THE SYSTEM SHALL รักษา file order เดิม
- 2.24  IF task ID ซ้ำ THEN THE SYSTEM SHALL fail closed พร้อม path, line และ diagnostic code
- 2.25  IF dependency อ้าง task ID ที่ไม่มีจริง THEN THE SYSTEM SHALL fail closed พร้อม path, line และ diagnostic code
- 2.26  IF dependency graph มี cycle THEN THE SYSTEM SHALL fail closed พร้อม path, line และ diagnostic code
- 2.27  IF task selector คลุมเครือ THEN THE SYSTEM SHALL fail closed พร้อม path, line และ diagnostic code
- 2.28  WHEN requirements criteria ถูก lint THE SYSTEM SHALL รับเฉพาะ EARS statement รูปเต็มทั้งห้ารูป
- 2.29  WHEN feature criterion ถูก lint THE SYSTEM SHALL require criterion major ให้ตรงกับ `REQ-N` heading
- 2.30  WHEN feature criterion ถูก lint THE SYSTEM SHALL require criterion ID ให้ unique
- 2.31  WHEN bugfix spec ถูก lint THE SYSTEM SHALL ตรวจ EARS ของ `F-N` ทุกตัว
- 2.32  WHEN bugfix spec ถูก lint THE SYSTEM SHALL ตรวจ EARS ของ `B-N` ทุกตัว
- 2.33  WHEN bugfix trace ถูกตรวจ THE SYSTEM SHALL require `F-N` ทุกตัวใน task `Satisfies:`
- 2.34  WHEN bugfix trace ถูกตรวจ THE SYSTEM SHALL require `B-N` ทุกตัวใน task `Satisfies:`
- 2.35  IF bugfix directory ไม่มี `requirements.md` THEN THE SYSTEM SHALL ยังคง lint และ trace `F/B` criteria
- 2.36  WHEN design trace ถูกตรวจ THE SYSTEM SHALL นับ coverage เฉพาะ reference ใน column ชื่อ exact `REQ`
- 2.37  WHEN design trace ถูกตรวจ THE SYSTEM SHALL require ค่าใน column `Section` ให้ตรงกับ real `##` heading แบบ exact และ case-sensitive
- 2.38  WHEN design trace ถูกตรวจ THE SYSTEM SHALL ไม่นับ heading ใน fenced code block
- 2.39  WHEN design trace ถูกตรวจ THE SYSTEM SHALL ไม่นับ reference จาก prose หรือ column อื่น
- 2.40  WHEN task references ถูกตรวจ THE SYSTEM SHALL อ่าน `Satisfies:` เฉพาะก่อน `Evidence:`
- 2.41  WHEN task references ถูกตรวจ THE SYSTEM SHALL อ่าน `Depends on:` เฉพาะก่อน `Evidence:`
- 2.42  WHEN task references ถูกตรวจ THE SYSTEM SHALL อ่าน `Verify:` เฉพาะก่อน `Evidence:`
- 2.43  WHEN task references ถูกตรวจ THE SYSTEM SHALL อ่าน `Batch:` เฉพาะก่อน `Evidence:`
- 2.44  WHEN spec slice รับ feature และ task ID ที่มีจริง THE SYSTEM SHALL คืน phase artifact statuses ก่อนข้อมูลส่วนอื่น
- 2.45  WHEN spec slice รับ feature และ task ID ที่มีจริง THE SYSTEM SHALL คืน task block แบบ verbatim
- 2.46  WHEN feature spec slice ถูกสร้าง THE SYSTEM SHALL คืน linked `REQ` content
- 2.47  WHEN bugfix spec slice ถูกสร้าง THE SYSTEM SHALL คืน linked `F/B` content
- 2.48  WHEN resolved design mapping มีอยู่ THE SYSTEM SHALL คืน mapped design sections
- 2.49  WHEN spec slice มี unresolved mapping THE SYSTEM SHALL คืน `MISSING:` diagnostics ตามลำดับคงที่
- 2.50  IF spec slice พบ unresolved mapping THEN THE SYSTEM SHALL ไม่ละ mapping นั้นโดยเงียบ
- 2.51  IF spec slice พบ unresolved mapping THEN THE SYSTEM SHALL ไม่เดา design section
- 2.52  WHEN state engine ตรวจ spec directory THE SYSTEM SHALL derive ค่าเดียวจาก `active`, `complete`, `superseded`, `blocked` หรือ `archived`
- 2.53  WHILE state engine derive state THE SYSTEM SHALL ใช้ artifact bytes และ canonical directory location เท่านั้น
- 2.54  WHEN SessionStart แสดง active specs THE SYSTEM SHALL เรียงชื่อแบบ lexical order
- 2.55  WHEN SessionStart แสดง state summary THE SYSTEM SHALL แสดงจำนวน blocked specs แบบย่อ
- 2.56  WHEN SessionStart แสดง state summary THE SYSTEM SHALL ไม่ list complete specs ทั้งหมด
- 2.57  WHEN SessionStart แสดง state summary THE SYSTEM SHALL ไม่ list superseded specs ทั้งหมด
- 2.58  WHEN SessionStart แสดง state summary THE SYSTEM SHALL ไม่ list archived specs ทั้งหมด
- 2.59  WHEN caller พบ `MISSING:` ใน spec slice THE SYSTEM SHALL ใช้ full-read fallback ก่อน downstream phase

## REQ-3: Evidence และ task completion gate แบบ fail-closed

**เรื่องราวผู้ใช้:** ในฐานะผู้ตรวจ task completion ฉันต้องการ Evidence และผล build/test ที่รันจริง เพื่อไม่ให้แผนหรือข้อความจาก task อื่นถูกนับเป็นหลักฐานผ่าน

**เกณฑ์รับงานแบบ EARS:**

- 3.1  WHEN completed task ถูกตรวจ THE SYSTEM SHALL require Evidence v2 ใน task block เดียวกัน
- 3.2  WHEN completed task ถูกตรวจ THE SYSTEM SHALL require execution observation ที่บันทึก command และ observed result
- 3.3  WHEN completed task ถูกตรวจ THE SYSTEM SHALL require `viewports` field
- 3.4  WHEN completed task ถูกตรวจ THE SYSTEM SHALL require `deviations` field
- 3.5  IF Evidence มีค่าที่สื่อว่ายังไม่เสร็จ THEN THE SYSTEM SHALL reject task completion
- 3.6  IF Evidence อยู่ใน sibling task THEN THE SYSTEM SHALL reject task completion ของ task ที่ขาด Evidence
- 3.7  IF execution observation ขาด command THEN THE SYSTEM SHALL reject task completion
- 3.8  IF execution observation ขาด observed result THEN THE SYSTEM SHALL reject task completion
- 3.9  IF Evidence ขาด `viewports` field THEN THE SYSTEM SHALL reject task completion
- 3.10  IF Evidence ขาด `deviations` field THEN THE SYSTEM SHALL reject task completion
- 3.11  IF Evidence ระบุเพียงแผนว่าจะรัน THEN THE SYSTEM SHALL reject task completion
- 3.12  WHEN task gate ทำงานที่ repo root และ `SDD_TYPECHECK_CMD` ว่าง THE SYSTEM SHALL ใช้ `dotnet build pol-core.slnx -warnaserror`
- 3.13  WHEN task gate ทำงานที่ repo root และ `SDD_TEST_CMD` ว่าง THE SYSTEM SHALL ใช้ `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"`
- 3.14  IF build command resolve ไม่ได้ THEN THE SYSTEM SHALL block task completion
- 3.15  IF test command resolve ไม่ได้ THEN THE SYSTEM SHALL block task completion
- 3.16  IF build command คืน non-zero THEN THE SYSTEM SHALL block task completion
- 3.17  IF test command คืน non-zero THEN THE SYSTEM SHALL block task completion
- 3.18  IF resolved test command รายงาน zero tests ตามพฤติกรรมปกติของ toolchain THEN THE SYSTEM SHALL ใช้ exit status จริงโดยไม่มี special pass rule
- 3.19  WHILE safe cache ถูกใช้ THE SYSTEM SHALL cache เฉพาะ observed green build result
- 3.20  WHILE safe cache ถูกใช้ THE SYSTEM SHALL cache เฉพาะ observed green test result
- 3.21  WHILE safe cache ถูกใช้ THE SYSTEM SHALL ไม่ cache Evidence verdict
- 3.22  WHEN `SDD_GATE_NO_CACHE=1` ถูกตั้ง THE SYSTEM SHALL รัน build command จริง
- 3.23  WHEN `SDD_GATE_NO_CACHE=1` ถูกตั้ง THE SYSTEM SHALL รัน test command จริง

## REQ-4: Shared consumers และ repository binding

**เรื่องราวผู้ใช้:** ในฐานะผู้ใช้ automation ฉันต้องการให้ pane-loop, cost parser และ GitHub sync ใช้ task graph เดียวกันและผูกกับ repository ปัจจุบัน เพื่อไม่ให้ numeric-only assumption หรือ manifest ข้าม repo ทำงานผิดเป้าหมาย

**เกณฑ์รับงานแบบ EARS:**

- 4.1  WHEN pane-loop อ่าน tasks THE SYSTEM SHALL ใช้ parsed string IDs จาก shared engine
- 4.2  WHEN cost parser อ่าน tasks THE SYSTEM SHALL ใช้ parsed string IDs จาก shared engine
- 4.3  WHEN GitHub sync อ่าน tasks THE SYSTEM SHALL ใช้ parsed string IDs จาก shared engine
- 4.4  WHEN shared consumer อ่าน alphanumeric task ID THE SYSTEM SHALL รักษา exact ID แบบ case-sensitive
- 4.5  IF shared consumer พบ unknown dependency THEN THE SYSTEM SHALL reject task graph
- 4.6  IF shared consumer พบ dependency cycle THEN THE SYSTEM SHALL reject task graph
- 4.7  WHEN GitHub sync resolve repository THE SYSTEM SHALL derive `owner/repo` จาก git remote `origin`
- 4.8  IF sync manifest ระบุ repository ไม่ตรงกับ current `origin` THEN THE SYSTEM SHALL block sync
- 4.9  WHILE GitHub sync ทำงาน THE SYSTEM SHALL ไม่ใช้ hardcoded source repository

## REQ-5: No-fabrication migration

**เรื่องราวผู้ใช้:** ในฐานะผู้ดูแล historical specs ฉันต้องการ migration ที่เปลี่ยนเฉพาะข้อมูลซึ่งมีหลักฐานปัจจุบันหรือประวัติรองรับ เพื่อไม่สร้าง approval หรือผลการรันที่ไม่เคยเกิดขึ้น

**เกณฑ์รับงานแบบ EARS:**

- 5.1  WHEN retrofit tool ทำ dry-run THE SYSTEM SHALL รายงาน actions แบบ deterministic sorted order
- 5.2  WHEN retrofit tool ทำ dry-run THE SYSTEM SHALL รายงาน blockers แบบ deterministic sorted order
- 5.3  WHEN retrofit tool รายงาน action หรือ blocker THE SYSTEM SHALL แนบ current evidence หรือ historical evidence ที่ใช้ตัดสิน
- 5.4  WHILE retrofit tool ทำ dry-run THE SYSTEM SHALL ไม่เขียนไฟล์
- 5.5  WHEN retrofit tool ใช้ `--apply-safe` THE SYSTEM SHALL require clean working tree ก่อนเริ่มเขียน
- 5.6  WHEN retrofit tool ใช้ `--apply-safe` THE SYSTEM SHALL capture `HEAD` ก่อนเริ่มเขียน
- 5.7  WHEN retrofit tool ใช้ `--apply-safe` THE SYSTEM SHALL capture file hash ของทุกไฟล์เป้าหมายก่อนเริ่มเขียน
- 5.8  WHEN retrofit tool เขียนไฟล์ THE SYSTEM SHALL ใช้ atomic replace
- 5.9  WHILE retrofit tool migrate task artifacts THE SYSTEM SHALL preserve task IDs เดิม
- 5.10  WHILE retrofit tool migrate task artifacts THE SYSTEM SHALL preserve task order เดิม
- 5.11  WHILE retrofit tool normalize historical text THE SYSTEM SHALL preserve legacy text เดิมแบบ verbatim
- 5.12  IF `HEAD` เปลี่ยนหลัง scan THEN THE SYSTEM SHALL หยุดโดยไม่เขียนไฟล์เป้าหมาย
- 5.13  IF file hash เปลี่ยนหลัง scan THEN THE SYSTEM SHALL หยุดโดยไม่เขียนไฟล์เป้าหมาย
- 5.14  IF status ไม่มี explicit current proof หรือ historical proof THEN THE SYSTEM SHALL report blocker
- 5.15  IF Evidence ไม่มี explicit current proof หรือ historical proof THEN THE SYSTEM SHALL report blocker
- 5.16  IF trace mapping ไม่มี explicit current proof หรือ historical proof THEN THE SYSTEM SHALL report blocker
- 5.17  WHILE retrofit tool migrate artifacts THE SYSTEM SHALL ไม่สร้าง approval ขึ้นเอง
- 5.18  WHILE retrofit tool migrate artifacts THE SYSTEM SHALL ไม่สร้าง command result ขึ้นเอง
- 5.19  WHILE retrofit tool migrate artifacts THE SYSTEM SHALL ไม่สร้าง viewport result ขึ้นเอง
- 5.20  WHILE retrofit tool migrate artifacts THE SYSTEM SHALL ไม่สร้าง deviation ขึ้นเอง
- 5.21  WHEN safe migration batch ถูก apply แล้ว THE SYSTEM SHALL ทำ dry-run รอบถัดไปโดยรายงาน safe changes เป็นศูนย์
- 5.22  WHEN strict CI cutover ถูกเสนอ THE SYSTEM SHALL require `--check` ให้ผ่านครบ 62 spec directories

## REQ-6: Adapter parity ตาม runtime capability จริง

**เรื่องราวผู้ใช้:** ในฐานะผู้ใช้หลาย agent harness ฉันต้องการ verdict ที่เท่ากันจาก input เดียวกัน โดยไม่อ้าง capability ที่ runtime ไม่มีจริง

**เกณฑ์รับงานแบบ EARS:**

- 6.1  WHEN Claude รับ normalized fixture THE SYSTEM SHALL คืน verdict หนึ่งค่าใน `allow`, `policy-fail` หรือ `engine-fail`
- 6.2  WHEN Codex รับ normalized fixture เดียวกัน THE SYSTEM SHALL คืน verdict เดียวกับ Claude
- 6.3  WHEN OpenCode รับ normalized fixture เดียวกัน THE SYSTEM SHALL คืน verdict เดียวกับ Claude
- 6.4  WHILE harness hook timing ต่างกัน THE SYSTEM SHALL รักษา verdict parity จาก normalized input เดียวกัน
- 6.5  WHEN Pi adapter docs อธิบาย pre-tool guard THE SYSTEM SHALL ระบุ capability เป็น floor-only หรือ unsupported ตาม runtime จริง
- 6.6  WHEN Pi adapter docs อธิบาย task gate THE SYSTEM SHALL ระบุ capability เป็น floor-only หรือ unsupported ตาม runtime จริง
- 6.7  WHEN Pi adapter docs อธิบาย subagents THE SYSTEM SHALL ระบุ capability เป็น floor-only หรือ unsupported ตาม runtime จริง
- 6.8  WHEN Pi adapter docs อธิบาย MCP หรือ browser THE SYSTEM SHALL ระบุ capability เป็น floor-only หรือ unsupported ตาม runtime จริง
- 6.9  WHILE Pi adapter ถูก align THE SYSTEM SHALL ไม่เพิ่มไฟล์ใต้ `.pi/extensions/**`

## REQ-7: CI cutover และ final verification

**เรื่องราวผู้ใช้:** ในฐานะผู้รักษา merge gate ฉันต้องการเปิด strict SDD checks หลัง migration ผ่านทั้งหมด โดยเปลี่ยนเฉพาะ verify paths และมีผลรันจริงครบก่อนส่งมอบ

**เกณฑ์รับงานแบบ EARS:**

- 7.1  WHEN GitHub verify path ทำงาน THE SYSTEM SHALL รัน Python unit tests ของ shared contract และ retrofit behavior
- 7.2  WHEN GitLab verify path ทำงาน THE SYSTEM SHALL รัน Python unit tests ของ shared contract และ retrofit behavior
- 7.3  WHEN GitHub verify path ทำงาน THE SYSTEM SHALL รัน shell fixtures ของ enforcement และ adapters
- 7.4  WHEN GitLab verify path ทำงาน THE SYSTEM SHALL รัน shell fixtures ของ enforcement และ adapters
- 7.5  WHEN verify path ตรวจ task completion THE SYSTEM SHALL ใช้ diff-aware Evidence selection
- 7.6  WHEN verify path ตรวจ spec tree THE SYSTEM SHALL รัน strict all-spec check
- 7.7  WHEN verify path ตรวจ trace THE SYSTEM SHALL ตรวจทั้ง feature `REQ` trace และ bugfix `F/B` trace
- 7.8  WHEN verify path ตรวจ adapters THE SYSTEM SHALL รัน policy alignment fixture
- 7.9  WHEN verify path ตรวจ adapters THE SYSTEM SHALL รัน cross-harness conformance fixture
- 7.10  WHILE CI cutover ถูก implement THE SYSTEM SHALL จำกัด workflow changes ไว้ใน GitHub และ GitLab verify paths
- 7.11  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า Python unit tests ผ่าน
- 7.12  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า shell fixtures ผ่าน
- 7.13  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า retrofit strict check ผ่าน
- 7.14  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า .NET restore ผ่าน
- 7.15  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า .NET build ผ่าน
- 7.16  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า .NET non-integration tests ผ่าน
- 7.17  WHEN final verification จบ THE SYSTEM SHALL แสดง observed output ว่า end-to-end SDD fixture ผ่าน

## REQ-8: Rollback และ environment limitations

**เรื่องราวผู้ใช้:** ในฐานะ operator ฉันต้องการ rollback unit ที่แยกชั้นและหลักฐานที่ซื่อสัตย์ต่อ environment เพื่อกู้คืน SDD migration โดยไม่แตะ product data หรืออ้าง remote verification ที่ยังไม่ได้รัน

**เกณฑ์รับงานแบบ EARS:**

- 8.1  IF CI cutover ทำ pipeline fail THEN THE SYSTEM SHALL rollback CI layer ก่อน layer อื่น
- 8.2  IF migration batch ให้ผลผิด THEN THE SYSTEM SHALL rollback เฉพาะ batch นั้น
- 8.3  WHEN migration batch ถูก rollback THE SYSTEM SHALL rerun retrofit dry-run ก่อนดำเนินต่อ
- 8.4  IF adapter หรือ enforcement layer ถูก rollback THEN THE SYSTEM SHALL คง migrated Markdown artifacts ที่ยัง valid
- 8.5  IF shared parser layer ถูก rollback THEN THE SYSTEM SHALL ไม่เปลี่ยน product runtime
- 8.6  WHILE strict contract ใช้งานหลัง cutover THE SYSTEM SHALL ไม่ใช้ dual-write status schema
- 8.7  WHILE strict contract ใช้งานหลัง cutover THE SYSTEM SHALL ไม่ใช้ dual-write Evidence schema
- 8.8  IF remote GitHub หรือ GitLab authorization ยังไม่พร้อม THEN THE SYSTEM SHALL ระบุ remote rule verification ว่ายังไม่ถูกสังเกตจริง
- 8.9  WHERE remote authorization ยังไม่พร้อม THE SYSTEM SHALL ใช้ static workflow validation และ local CI-equivalent checks เป็นหลักฐานชั่วคราวที่ระบุขอบเขตชัด
- 8.10  WHERE Docker หรือ SQL verification ต้องเข้าถึง resource ที่ sandbox ปิดกั้น THE SYSTEM SHALL รันเฉพาะ check ที่เกี่ยวข้องนอก sandbox
- 8.11  WHILE environment verification ทำงาน THE SYSTEM SHALL ไม่อ่าน credential จาก `.env` หรือ secret files
- 8.12  WHERE SQL verification ต้องใช้ credential THE SYSTEM SHALL สร้าง ephemeral credential ใน test environment
- 8.13  IF environment limitation ทำให้ check ใดรันไม่ได้ THEN THE SYSTEM SHALL บันทึก check นั้นเป็น unverified พร้อมเหตุผล

## REQ-9: Adversarial property และ bypass resistance

**เรื่องราวผู้ใช้:** ในฐานะผู้ดูแล enforcement floor ฉันต้องการให้ทุก consumer ตัดสิน input class เดียวกันเหมือนกันและให้ invalid input fail closed เพื่อปิด regex side path และ adapter-specific bypass

**เกณฑ์รับงานแบบ EARS:**

- 9.1  THE SYSTEM SHALL คืน verdict เดียวกันทุก consumer สำหรับ normalized input class เดียวกัน
- 9.2  IF normalized input ผิด canonical contract THEN THE SYSTEM SHALL fail closed
- 9.3  WHILE shared contract ถูกใช้ THE SYSTEM SHALL ไม่มี regex side path ที่ให้ verdict ขัดกับ engine
- 9.4  WHILE adapters ถูกใช้ THE SYSTEM SHALL ไม่มี adapter-specific path ที่ให้ invalid artifact เป็น green verdict
- 9.5  IF adapter ตัด shared engine call ออก THEN THE SYSTEM SHALL ทำให้ conformance mutation test fail
- 9.6  IF build หรือ test เป็น red THEN THE SYSTEM SHALL ไม่มี guard input รูปแบบใดเปลี่ยนผลเป็น green verdict
- 9.7  WHEN guard รับ command input THE SYSTEM SHALL normalize shell separators, substitution forms, env prefix, absolute binary path และ git global options ก่อนตัดสิน
- 9.8  WHEN normalized guard commands มี semantics เดียวกัน THE SYSTEM SHALL คืน verdict เดียวกัน

## Adversarial input classes

ตารางนี้กำหนด observable verdict ของ 42 input classes โดยอ้าง stable requirement IDs และไม่กำหนดวิธี implement parser หรือ adapter

| กรณี | Input class | Observable verdict | Requirement |
|---:|---|---|---|
| 1 | Canonical approved status อยู่นอก fence | parse เป็น approved | `REQ-2.1`, `REQ-2.2` |
| 2 | Approved status มี suffix annotation | block และไม่อนุมาน approval | `REQ-2.1`, `REQ-2.5` |
| 3 | Status อยู่ใน fenced code block | ไม่นับ status | `REQ-2.2` |
| 4 | Status ซ้ำสองบรรทัด | block | `REQ-2.3`, `REQ-2.6` |
| 5 | Status alias นอก canonical grammar | block ใน strict mode | `REQ-2.1`, `REQ-2.5` |
| 6 | Task IDs `1`, `A1`, `D`, `migration-2`, `api_v2` | parse exact ทุกค่า | `REQ-2.21` |
| 7 | Task ID ซ้ำต่างบรรทัด | block พร้อม location และ code | `REQ-2.24` |
| 8 | IDs `A1` และ `a1` | valid เป็นคนละ ID | `REQ-2.21` |
| 9 | Numeric selector `1-3` และ endpoint IDs มีจริง | expand ตาม file order | `REQ-2.23`, `REQ-2.27` |
| 10 | Literal task ID `1-3` มีจริง | exact ID ชนะ range interpretation | `REQ-2.21`, `REQ-2.27` |
| 11 | Unknown dependency | block | `REQ-2.25`, `REQ-4.5` |
| 12 | Dependency cycle | block | `REQ-2.26`, `REQ-4.6` |
| 13 | Evidence อยู่ใน sibling task | task ที่ขาด Evidence ยัง block | `REQ-3.1`, `REQ-3.6` |
| 14 | Evidence มี unfinished marker ตาม invalid fixture set | block | `REQ-3.5` |
| 15 | Evidence ไม่มี command หรือ observed result | block | `REQ-3.7`, `REQ-3.8` |
| 16 | Non-UI viewport เป็น bare `n/a` | block | `REQ-3.3`, `REQ-3.9` |
| 17 | UI viewport ขาด 375, 768 หรือ 1440 | block | `REQ-3.3`, `REQ-3.9` |
| 18 | Evidence transcript มี `Satisfies:` | ไม่นับ coverage | `REQ-2.40` |
| 19 | Criterion มีเพียง `WHEN` | EARS lint fail | `REQ-2.28` |
| 20 | `IF` ไม่มี `THEN THE SYSTEM SHALL` | EARS lint fail | `REQ-2.28` |
| 21 | Version, duration หรือ prose มี dotted number | ไม่ parse เป็น requirement reference | `REQ-2.36`, `REQ-2.39` |
| 22 | Cross-REQ range | block | `REQ-2.36`, `REQ-2.39` |
| 23 | Trace reference อยู่ prose หรือ column อื่น | ไม่นับ | `REQ-2.36`, `REQ-2.39` |
| 24 | `Section` ต่าง case หรือ match เพียง substring | block | `REQ-2.37` |
| 25 | Heading ปลอมอยู่ใน code fence | ไม่นับ | `REQ-2.38` |
| 26 | Code fence ไม่ปิด | parse error และ block | `REQ-2.2`, `REQ-9.2` |
| 27 | Bugfix มี unmapped `F-N` หรือ `B-N` | block | `REQ-2.33`, `REQ-2.34` |
| 28 | Slice รับ unknown task | fail พร้อม available IDs ตาม file order | `REQ-2.23`, `REQ-2.44` |
| 29 | Slice รับ known task แต่ mapping ขาด | success พร้อม `MISSING:` และ caller ใช้ full-read fallback | `REQ-2.49`, `REQ-2.50`, `REQ-2.59` |
| 30 | Empty spec directory | state เป็น blocked | `REQ-2.52`, `REQ-2.53` |
| 31 | Requirements และ bugfix artifacts อยู่ directory เดียวกัน | state เป็น blocked | `REQ-2.52`, `REQ-2.53` |
| 32 | Command env ว่างและพบ `pol-core.slnx` | ใช้ .NET defaults | `REQ-3.12`, `REQ-3.13` |
| 33 | Command env ว่างและไม่พบ project | engine error และ block | `REQ-3.14`, `REQ-3.15` |
| 34 | Build หรือ test คืน non-zero | block พร้อม diagnostic output ที่เกี่ยวข้อง | `REQ-3.16`, `REQ-3.17` |
| 35 | Cache hit แต่ Evidence fail | block | `REQ-3.21` |
| 36 | CI merge-base resolve ไม่ได้หรือ zero SHA จัดการไม่ได้ | engine error และ block | `REQ-7.5`, `REQ-9.2` |
| 37 | `--apply-safe` ทำงานบน dirty tree | หยุดโดยไม่เขียน | `REQ-5.5` |
| 38 | `HEAD` หรือ file hash เปลี่ยนระหว่าง scan และ write | หยุดโดยไม่เขียน | `REQ-5.12`, `REQ-5.13` |
| 39 | Historical proof ขัดกัน | blocker และไม่ auto-approve | `REQ-5.14`, `REQ-5.17` |
| 40 | Manifest repository ไม่ตรง current `origin` | block | `REQ-4.8` |
| 41 | Adapter ตัด engine call ออกหนึ่งจุด | conformance mutation เป็น red | `REQ-9.5` |
| 42 | Guard input ใช้ shell separators, substitutions, env prefix, absolute binary path หรือ git global options | normalize แล้วตัดสินตาม command semantics เดียวกัน | `REQ-9.7`, `REQ-9.8` |

## Verification matrix

หลักฐานทุกแถวต้องเป็น observed output จาก command หรือ diff ที่รันจริง ห้ามใช้ implementation summary แทน proof

| แหล่งที่มา | Requirement | Observable proof |
|---|---|---|
| `AC-1` | `REQ-2.1` ถึง `REQ-2.3` | Status fixtures แสดง canonical pass, fenced status ถูก ignore และ duplicate status block |
| `AC-2` | `REQ-2.4` ถึง `REQ-2.11` | Phase-gate fixtures แสดง missing, malformed, conflict และ unknown block โดย checkbox, code, commit และ conversation ไม่เปลี่ยน verdict |
| `AC-3` | `REQ-2.12` ถึง `REQ-2.20` | Workflow fixtures ครบ Requirements-first, Design-first และ Bugfix แสดง upstream approval และ trace gate ต่อ phase |
| `AC-4` | `REQ-2.21` ถึง `REQ-2.23` | Task parser fixtures แสดง numeric และ alphanumeric IDs แบบ case-sensitive พร้อม byte value และ file order เดิม |
| `AC-5` | `REQ-2.24` ถึง `REQ-2.27` | Invalid task graph fixtures คืน non-zero พร้อม path, line และ diagnostic code |
| `AC-6` | `REQ-3.1` ถึง `REQ-3.4` | Completed-task fixtures ผ่านเฉพาะ Evidence v2 ใน block เดียวที่มี execution observation, viewports และ deviations |
| `AC-7` | `REQ-3.5` ถึง `REQ-3.11` | Negative Evidence fixtures ทุก failure class คืน block verdict |
| `AC-8` | `REQ-3.12`, `REQ-3.13` | Gate fixture ที่ unset command env แสดง exact .NET default commands ที่ถูก execute |
| `AC-9` | `REQ-3.14` ถึง `REQ-3.18` | Command-resolution, build-fail, test-fail และ zero-test fixtures แสดง fail-closed ตาม exit status จริง |
| `AC-10` | `REQ-3.19` ถึง `REQ-3.23` | Cache fixtures แสดงเฉพาะ green build/test ถูก reuse, Evidence ถูกตรวจทุกครั้ง และ no-cache บังคับ execution จริง |
| `AC-11` | `REQ-2.28` ถึง `REQ-2.30` | EARS fixtures ครบ 5 forms และ near-miss แสดง major mismatch กับ duplicate ID เป็น red |
| `AC-12` | `REQ-2.31` ถึง `REQ-2.35` | Bugfix fixtures แสดง `F/B` lint และ trace ทำงานแม้ไม่มี `requirements.md` |
| `AC-13` | `REQ-2.36` ถึง `REQ-2.39` | Trace fixtures แสดง named columns, exact real heading และ fence-aware behavior |
| `AC-14` | `REQ-2.40` ถึง `REQ-2.43` | Task-block fixtures แสดง fields หลัง Evidence ไม่ถูก parse เป็น references |
| `AC-15` | `REQ-2.44` ถึง `REQ-2.49` | Feature และ bugfix slice golden outputs แสดง status, verbatim task, linked criteria, design sections และ diagnostics ตามลำดับ |
| `AC-16` | `REQ-2.49` ถึง `REQ-2.51`, `REQ-2.59` | Missing-mapping fixture แสดง `MISSING:` โดยไม่มี silent omission หรือ guessed section และ caller ใช้ full-read fallback |
| `AC-17` | `REQ-2.52`, `REQ-2.53` | State fixtures แสดงทั้ง 5 states จาก artifact bytes และ canonical directory location อย่าง deterministic รวม `archived` ใต้ `.ai/specs/archive/` |
| `AC-18` | `REQ-2.54` ถึง `REQ-2.58` | SessionStart fixture แสดง lexical active list กับ blocked count และไม่ dump inactive lists |
| `AC-19` | `REQ-4.1` ถึง `REQ-4.6` | pane-loop, cost parser และ sync fixtures ใช้ alphanumeric IDs และ reject invalid dependency graph เหมือนกัน |
| `AC-20` | `REQ-4.7` ถึง `REQ-4.9` | Git remote fixtures แสดง derive current `owner/repo`, mismatch block และไม่มี hardcoded repo path |
| `AC-21` | `REQ-5.1` ถึง `REQ-5.4` | Dry-run golden output เรียง actions และ blockers คงที่ พร้อม proof และ clean-tree diff เป็นศูนย์ |
| `AC-22` | `REQ-5.5` ถึง `REQ-5.13` | Safe-apply fixtures แสดง dirty-tree block, captured HEAD/hash, atomic replace, preservation และ concurrent-change stop |
| `AC-23` | `REQ-5.14` ถึง `REQ-5.20` | No-proof และ conflicting-history fixtures แสดง blocker โดยไม่มี fabricated status หรือ Evidence fields |
| `AC-24` | `REQ-5.21`, `REQ-5.22` | Applied batch มี second dry-run เป็น no-op และ strict check รายงาน 62 directories ผ่านก่อน CI cutover |
| `AC-25` | `REQ-6.1` ถึง `REQ-6.4` | Cross-harness conformance fixture แสดง Claude, Codex และ OpenCode verdict เท่ากันทุก normalized case |
| `AC-26` | `REQ-6.5` ถึง `REQ-6.9` | Policy-alignment fixture เทียบ Pi docs กับ runtime matrix และยืนยันไม่มี `.pi/extensions/**` |
| `AC-27` | `REQ-1.12` ถึง `REQ-1.17` | Canonical-doc alignment fixture เทียบ modules, DbContexts, isolation, CI jobs, handoff schema และ git boundaries กับ filesystem/config จริง |
| `AC-28` | `REQ-7.1` ถึง `REQ-7.10` | GitHub/GitLab workflow inspection และ local CI-equivalent run แสดง checks ใหม่อยู่เฉพาะ verify paths |
| `AC-29` | `REQ-1.7` ถึง `REQ-1.11` | Workflow diff guard แสดง protected product, package และ deploy jobs byte-equivalent ในส่วน command/image/service/secret/semantics |
| `AC-30` | `REQ-7.11` ถึง `REQ-7.17` | Final verification record เก็บ exact commands กับ observed pass counts ของ Python, shell, retrofit, .NET และ end-to-end SDD fixture |
| `AC-31` | `REQ-1.1` ถึง `REQ-1.6` | Product scope guard แสดงไม่มี changed path ใน forbidden product/runtime set |
| `AC-32` | `REQ-9.1` ถึง `REQ-9.8` | Adversarial suite ทั้ง 42 cases และ required mutations แสดง invalid artifacts, red commands และ adapter bypass attempts ไม่เป็น green |
| Rollback | `REQ-8.1` ถึง `REQ-8.7` | Layer และ batch rollback rehearsal แสดง revert unit ถูกชั้น, dry-run หลัง rollback และไม่มี dual-write schema |
| Environment limitations | `REQ-8.8` ถึง `REQ-8.13` | Verification report แยก local proof, remote unverified state, unsandboxed Docker/SQL checks และ ephemeral credential usage ชัดเจน |

## ข้อจำกัดของ environment

- Environment ที่ตรวจพบมี Python 3.14.5, .NET SDK 10.0.300, Docker client/daemon 29.4.0, `sqlcmd`, `jq`, Codex และ OpenCode
- GitHub และ GitLab server-side rules หรือ deploy behavior ต้องใช้ remote authorization ภายหลัง จึงห้ามอ้างว่า verified ก่อนมี observed remote output
- Docker และ SQL checks ที่ sandbox ปิด resource access ให้รันนอก sandboxเฉพาะ command ที่เกี่ยวข้อง
- ห้ามอ่าน `.env` หรือ secret files การทดสอบ SQL ใช้ ephemeral credentials ใน test environment เท่านั้น

## สถานะการตัดสินใจ

- เอกสารนี้คงสถานะ `approved 2026-08-25` พร้อม amendment ที่บันทึกใน `Status-Note`
- ไม่มีการอนุมาน approval จาก `.pipeline` artifacts, conversation, code, checkbox หรือ git history
- ไม่มี requirement ที่เปิด product runtime change, dependency ใหม่, deploy หรือ release execution
