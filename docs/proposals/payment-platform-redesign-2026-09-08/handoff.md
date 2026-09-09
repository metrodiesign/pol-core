# Handoff Note: แบบเรียบง่ายสำหรับทีมเล็ก

ฉบับสำหรับลงมือรุ่นแรกอยู่ที่ [platform-restructure-v1](../../../.ai/specs/platform-restructure-v1/handoff.md) พร้อม requirements, design และ tasks ให้ใช้ defaults ใน spec นั้นเมื่อแตกต่างจากข้อเสนอหน้านี้

สถานะส่งต่อวันที่ 9 กันยายน 2026 ชุดทำงานเหลือ blueprint, ผลตรวจ source, ERD v3 และ handoff นี้

## Task Summary

ผู้ใช้ต้องการลดความซับซ้อนให้ทีมประสบการณ์น้อยดูแลได้ และอนุญาตให้ลบส่วนที่ไม่ใช้ ขอบเขตที่ตกลงก่อนหน้ายังเป็นจัดแบบก่อน code รอบนี้จึงปรับแนวทาง implementation และลบเอกสารที่ถูกแทนแล้ว ไม่ refactor backend หรือเปลี่ยนฐานข้อมูล

## Current Status

done — ปรับแผนเรียบง่ายและลบเอกสารเก่า 6 ไฟล์แล้ว Source จริงยังมี 39 projects และ tests 14 projects การลดเหลือ 4/3 เป็นเป้าหมายที่เสนอ ยังไม่ใช่ผล refactor ที่ทำแล้ว

## Files Changed

| ไฟล์ที่คงใช้ | หน้าที่ |
|---|---|
| `docs/proposals/payment-platform-redesign-2026-09-08/latest-blueprint.md` | แบบหลัก รวมการลด project/layer wrappers และ MVP defaults |
| `docs/proposals/payment-platform-redesign-2026-09-08/latest-source-review.md` | ความตรงกับชุด pol-platform-latest และประเด็นที่ต้องล็อก |
| `outputs/diagrams/2026-09-08_payment-platform-latest-blueprint_v3.md` | Conceptual Model ทั้ง scope ไม่บังคับสร้างทุก entity/API ในรุ่นแรก |
| `docs/proposals/payment-platform-redesign-2026-09-08/handoff.md` | สถานะส่งต่อและข้อจำกัด |

เอกสารที่ลบเป็นผลงานเก่าของงานนี้ซึ่งถูกแทนและระบุ superseded แล้ว: overview.md, analysis.md, target-structure.md, payment-platform-business_v1.md, payment-platform-target-structure_v1.md และ payment-platform-latest-blueprint_v2.md ไม่มีไฟล์ต้นฉบับใน Downloads ถูกลบ

## Important Decisions

- คง 7 เจ้าของงานและไม่มี Payment aggregate แต่ลดการแยก project ซ้ำต่อโมดูล
- เป้าหมาย 4 source projects: Domain, Application, Infrastructure, Api และ 3 test projects: Unit, Architecture, Integration
- เสนอ 2 runtime contexts คือ Control Plane/Commerce หลังพิสูจน์ isolation/transaction ครบ Migration composition ใช้ mapping เจ้าของเดียว ไม่คัดลอก field/index
- Backend/DB/process หลักชุดเดียว งานเบื้องหลังใช้ BackgroundService และ outbox ที่มีอยู่ ไม่เพิ่ม broker/Redis/framework เผื่ออนาคต
- เลือกหนึ่ง active MerchantAccess ต่อ Account+Merchant เป็น default รุ่นแรก หากสิทธิ์ที่ต้องการแสดงไม่ได้ ห้ามเพิ่ม scope กว้างเพื่อให้ผ่าน
- เริ่มหนึ่ง Client ต่อ SYSTEM Account แต่ไม่ตัด SYSTEM การยืนยัน Client ต้องเลือกมาตรฐานหนึ่งวิธีและพิสูจน์ก่อนเปิดใช้
- เลื่อน OTP และส่วนเสริมที่ไม่มี use case ยืนยัน ไม่ตัดการตรวจ contact ที่จำเป็น
- คง 2C2P/Omise, Email+SMS, auth/tenant guards, vault, idempotency, snapshots, audit, inbox/outbox, retry และการรับผลเงินมาช้า
- ใช้ OAuth/OIDC component ที่ดูแล ไม่สร้าง authorization server หรือ crypto เอง และไม่เก็บ session/token state ซ้ำกับ component
- 116 API เป็น inventory scope เต็ม ต้อง trace แต่ละ API ไปยัง use case/consumer ก่อน implement ไม่ทำ CRUD ตามจำนวนตาราง
- ก่อนลบ source ต้องตรวจ consumer, DI/reflection/generated wiring, jobs/config, ข้อมูลและ callback ที่ค้าง แล้วรัน gate ที่กระทบ

## Constraints

- repo `/Users/king_developer/Desktop/Project/pol-core`, develop, baseline `6950ca4c`
- มีเฉพาะเอกสาร untracked ของงานนี้ ไม่มี source/schema/config หรือ production data เปลี่ยน
- ไม่ commit/push และไม่ลบตาราง ประวัติ PSP reference หรือข้อมูลผู้ใช้
- ค่าเริ่มต้นรุ่นแรกเป็นข้อเสนอจากคำขอลดความซับซ้อน ไม่ใช่ approval metadata หรือมติ Physical DDL

## Tests Run

นับไฟล์ project จริงด้วย pathlib: `source_projects=39`, `test_projects=14` architect ตรวจ 3 runtime contexts และ 1 migration composition ก่อนเสนอเป้าหมาย

ตรวจเอกสารและลิงก์หลัง cleanup:

```sh
python3 /Users/king_developer/.codex/tmp/payment-platform-redesign-2026-09-08/check_docs.py
```

ผลจริง: `PASS: 4 files, 19 local links, 0 errors`; `git diff --check` ผ่าน และไม่มี diff ใน src

ERD graph ไม่เปลี่ยนในรอบลดความซับซ้อน มีเพียงหมายเหตุ scope จึงใช้ผล render 6 ภาพที่ตรวจจริงรอบก่อน ไม่อ้างว่ารันใหม่ การตรวจ bundle ก่อนหน้าได้ SHA256 ตรง 40/40 และ API 116 records ตรงกันระหว่าง Markdown/JSON/HTML

ไม่ได้รัน application build/test เพราะไม่มี application code เปลี่ยน ไม่มีการอ้างว่า source ที่ถูกเสนอให้ถอดพิสูจน์ว่า unused แล้ว

## Known Issues

- ต้องทำ Physical Model และ state matrix create/issue/cancel ก่อนแก้ code
- การลด project/context ยังต้องมี architecture/integration evidence โดยเฉพาะ tenant filters, write guard และ cross-module transaction
- การใช้งานจริงของ legacy APIs, jobs, consumers และข้อมูล production ยังไม่ได้สำรวจพอจะลบ source
- Registration uniqueness, Employee scope, client authentication, ราคา/ภาษีและ operational TTL/SLA ยังต้องล็อกตาม use case

## Next Recommended Agent

orchestrator เมื่อผู้ใช้สั่ง implementation ให้ขับ spec และ migrator ทีละชุดพร้อม audit/verify/review ห้ามถือว่าเป้าหมายจำนวน project เป็นเหตุให้ตัด security boundary

## Next Steps

1. ใช้ blueprint §8–9 เลือก MVP และ physical packaging
2. ทำ inventory consumer/ข้อมูลจริงก่อนจัดชุดลบหรือย้าย source
3. ย้ายทีละส่วนให้ build/tests ผ่าน และ retire legacy เมื่อไม่มีผู้ใช้หรือรายการค้าง
