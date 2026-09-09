# Handoff: POL Platform รุ่นแรก

Spec สำหรับเริ่มพัฒนาเป็นช่วง โดยรักษาข้อมูลและลดโครงสร้างให้ทีมเล็กดูแลได้ เริ่มอ่าน [ลำดับงาน](tasks.md) แล้วใช้ [ข้อกำหนด](requirements.md) และ [แบบระบบ](design.md) ของช่วงนั้น

## ขอบเขตและสถานะ

| เรื่อง | สถานะ |
|---|---|
| Requirements | approved 2026-09-09, 12 กลุ่ม รวม 126 เกณฑ์ |
| Design | approved 2026-09-09, 7 เจ้าของงาน, 4 source projects, 3 test projects, 2 runtime contexts |
| Tasks | approved 2026-09-09, 10 ช่วงเรียง dependency, ยังไม่เริ่มทุกช่วง |
| API scope | [api-scope.json](api-scope.json), 111 v1 และ 5 deferred จาก inventory 116 รายการ |
| Source/config/database | ไม่มีการแก้ในรอบสร้าง spec |
| Commit/push/deploy | ไม่ได้ทำ |

ผู้ใช้อนุมัติ spec รุ่นแรกวันที่ 9 กันยายน 2026 พร้อมคำสั่ง “ยังไม่ต้องเริ่มทำ” การอนุมัตินี้ครอบคลุมเอกสารเท่านั้น หยุดรอคำสั่งเริ่ม implementation ทุก task ยังไม่เริ่ม

## ค่าที่ล็อกสำหรับ implementation

- หนึ่ง active MerchantAccess ต่อ Account/Merchant และหนึ่ง Client ต่อ SYSTEM Account ที่ผูก Merchant/environment เดียว
- Account/Access ใช้ OAuth state เจ้าของเดียวผ่าน OpenIddict ตาม version/EF compatibility ใน design
- Order เป็นเจ้าของยอดและสถานะธุรกิจ ไม่มี Payment aggregate; Transaction เก็บความจริงทุก attempt รวมผลมาช้าและเงินจริงซ้ำ
- ใช้ maintenance cutover เดียว, deterministic legacy mapping และ recovery ที่รักษา events หลัง cutover
- OTP และ template editor เลื่อนไปก่อน ส่วน Email/SMS ยังอยู่ใน scope พร้อม capability gate

Spec นี้เป็นรายละเอียด v1 ที่ใหม่กว่า [blueprint](../../../docs/proposals/payment-platform-redesign-2026-09-08/latest-blueprint.md) และ [conceptual ERD](../../../outputs/diagrams/2026-09-08_payment-platform-latest-blueprint_v3.md) หาก default ของ proposal เก่าต่างกัน ให้ใช้ spec นี้สำหรับแผน implementation ทั้งคู่ยังเป็นเอกสารออกแบบ ไม่ใช่หลักฐานระบบที่ใช้งานจริง

## หลักฐานตรวจเอกสาร

| การตรวจ | ผล |
|---|---|
| EARS และ reverse trace | 126/126 เกณฑ์อ้างครบใน design/tasks |
| API inventory | source hash ตรง, 116 records เดิมไม่เปลี่ยน, method/path ไม่ซ้ำ, 111 v1/5 deferred |
| Task dependency | 10 tasks, dependency ย้อนถึงงานก่อนหน้าเท่านั้น, ไม่มี task ทำเครื่องหมายเสร็จ |
| Mermaid | render ผ่านทั้ง 7 แผนภาพใน browser |
| Independent critique | PASS ระดับ design หลังแก้ findings 6 ข้อและตรวจซ้ำ ไม่มีข้อค้างจากรอบนี้ |
| รูปแบบและ local links | 8 เอกสาร, 27 links, 0 errors |

Review ปิดเรื่อง endpoint classes, CSRF ทุก cookie mutation, authorization lease ก่อน commit, terminal payment reducer/Order serialization, SFS และ exact write DTOs ผลนี้ไม่ใช่ runtime PASS

คำสั่งตรวจ trace:

```sh
bash scripts/spec-trace.sh platform-restructure-v1
python3 scripts/spec_contract.py task-ids --feature platform-restructure-v1 --all --format json
```

ผลก่อนอนุมัติ: phase design/tasks และ strict contract เคยคืน `PHASE_UPSTREAM_NOT_APPROVED` เพราะ artifacts เป็น draft ปัจจุบันบันทึก approval ตามคำยืนยันจริงของผู้ใช้แล้ว แยก Satisfies/Depends on/Verify คนละบรรทัดเพื่อให้ parser อ่านครบ โดยไม่เปลี่ยนขอบเขตงาน ผลตรวจ strict contract และ reverse trace ผ่าน 126/126 เกณฑ์ (exit 0) ไม่มีการเริ่ม implementation

ไม่ได้รัน application build/test, restore OpenIddict/EF หรือ Entra/PSP/SMS จริงในรอบเอกสาร คำสั่งใน tasks เป็น verification ที่ implementation ต้องทำ ไม่ใช่ Evidence ที่เกิดขึ้นแล้ว

## Inputs และขอบเขตที่ยังต้องตรวจ

| Input | ผลต่อการเริ่มงาน |
|---|---|
| SMS vendor/API | เริ่ม orchestration ได้ แต่เปิดส่งจริงไม่ได้จนมี adapter และ contract evidence |
| Entra/PSP credentials และ tenant/provider contracts | ทดสอบ local ได้; live capability ต้องผ่านหลักฐานภายนอก |
| master identity/Merchant mappings และ consumer inventory | เป็นงาน task 1/9; ห้ามเดาหรือลบ legacy ล่วงหน้า |
| production backup/recovery/authorization | ยังไม่ได้รับและไม่จำเป็นต่อการเขียน spec |

## ส่งต่องาน

สถานะปัจจุบัน: รอคำสั่งเริ่มงานจากผู้ใช้ เมื่อได้รับคำสั่งจึงเริ่ม task 1: เก็บ baseline และรวม packaging โดยรักษา behavior เดิมก่อนย้าย business contract ทีมต้องอ่าน requirements/design ของแต่ละ task และบันทึก Evidence จริงก่อน checkpoint

เอกสารนี้ไม่อนุญาตให้เริ่ม implementation เองในรอบที่ผู้ใช้ขอเฉพาะ spec และไม่อนุญาต production cutover การย้ายจริงต้องทำตาม task 9 และผ่านเงื่อนไขที่ระบุครบ
