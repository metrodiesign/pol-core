# HANDOFF: checkout-chain-document-fields

Rolling handoff — teammate ทุกคน **append หัวข้อใหม่ท้ายไฟล์** หลังจบ task ของตัวเอง (สิ่งที่ทำ / ไฟล์ที่แตะ / กับดักที่เจอจริง / สถานะ test / สิ่งที่คนถัดไปต้องรู้). ห้ามแก้หัวข้อของคนก่อน

## จาก lead (Fable) — บริบทตั้งต้น 2026-07-30

- Branch `feat/products-insurance-document`, PR #143 OPEN (Products pivot เป็นเอกสารประกัน merge แล้วใน branch นี้, commit `9e87c51`)
- Bridge ที่ต้องถอดอยู่ `src/Modules/Products/Products.Application/ProductView.cs` (~:47-60): `Price`/`SumInsured`/`CoverageDurationDays`/`Insurer` — จุดใช้มี 2 ที่ใน `src/Hosts/Api/Program.cs`: cart (`~:663-668` ใช้ `IsActive`+`Price`) และ checkout (`~:770-780` snapshot 3 field เก่า)
- ตำแหน่งไฟล์ทั้งหมด + ลำดับ param กลาง + traps: อ่าน `design.md` (สำรวจยืนยันแล้ว 2026-07-30 — เชื่อได้ ไม่ต้อง re-explore กว้าง)
- 3 field เก่าไม่มี logic คำนวณอิงอยู่ — ลบได้ตรง ๆ; policy report / OrderSummaryReader ไม่แตะ — ห้ามไปยุ่ง
- dev DB :11433 สถานะ: migration `20260730072057_ProductsInsuranceDocument` apply แล้ว, shop.Products = 100 (seed-demo) + 6 (e6 migration seed); หมายเหตุ: seed-demo assert `merch.Merchants = 0` fail เป็นสภาพ local เดิม (merchant `vprivilege` คนละ Id ค้าง) ไม่เกี่ยวงานเรา
- Follow-up ที่จดไว้แล้ว ไม่ทำรอบนี้: wire `Product.MarkPaid`/`Deactivate` ตอน order paid; SP adapter
