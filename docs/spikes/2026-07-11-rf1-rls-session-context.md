# Spike: RLS session context + cross-schema predicate (rf1 task #1 gate)

> Throwaway probe ที่ tasks.md (rf1-schema-reset) กำหนดเป็น gate ก่อน task 3 (cutover จริง).
> วันที่: 2026-07-11 · ผล: **PASS (GO)** — design ไปต่อได้ ไม่ต้องแก้

## คำถามที่ต้องตอบ

design.md (RLS section + Interceptor contract) พึ่งกลไก SQL Server 2025 สามอย่างที่ไม่เคยพิสูจน์บนของจริงมาก่อน
(ของเดิม single-schema, single-key, ไม่มี tier branch):

(ก) `sp_set_session_context @read_only=1` สองคีย์ (MerchantId, UserId) บน pooled connection — ค่าเก่ารั่วข้าม
physical connection reuse ไหม แล้ว re-stamp คีย์เดิมซ้ำได้จริงไหม (T13)
(ข) predicate function ใน schema `sec` อ้างตารางข้าม schema `admin.PlatformUsers`/`admin.PlatformMerchantAccess`
แบบ `WITH SCHEMABINDING` — compile ได้จริงไหม และ ownership chaining (ทุก schema `AUTHORIZATION dbo`) ทำให้ผู้ query
ไม่ต้องมี SELECT ตรงบน `admin.*` จริงไหม (REQ-3.10)
(ค) tier-based Super branch (เช็ค `PlatformUsers.Tier = 1` จริง แทน absence-in-PMA แบบเดิม) + REQ-3.11 fail-closed
(Scoped ไม่มีแถว PMA เห็นศูนย์) ทำงานตามที่ /spec-analyze finding F2 ตัดสินไว้ไหม

ถ้าข้อไหน no-go ต้องหยุดแล้วกลับไปแก้ design ก่อน task 3 (ตาม tasks.md).

## วิธี

throwaway ทั้งหมด ไม่มี code เข้า repo:

- container แยกต่างหาก `pol-spike-rf1-sql` (SQL Server 2025, `mcr.microsoft.com/mssql/server:2025-latest`,
  password สุ่มเอง, host port `11435`) — ไม่แตะ `pol-sql` (`:11434`) ที่ integration suite ใช้จริง และไม่ต้องอ่าน
  `.env.integration` (permission-denied สำหรับ agent — เลี่ยงโดยตั้ง credential ของ spike เอง แยกขาดจากของจริง)
- (ข)+(ค): `.sql` script รันผ่าน `sqlcmd` — สร้าง schema `sec`/`admin`/`shop` (`AUTHORIZATION dbo`), ตาราง
  `admin.PlatformUsers`/`admin.PlatformMerchantAccess`/`shop.TestRows`, ก็อป `sec.fn_merchant_predicate` จาก
  design.md มาตรง ๆ (ไม่ได้เขียนใหม่ — ต้อง SQL เดียวกับที่ task 3 จะใช้จริง), security policy, principal ทดสอบ
  `spike_app` (`WITHOUT LOGIN`, GRANT SELECT เฉพาะ `shop.TestRows` — ไม่แตะ `admin.*` เลย) แล้วรัน 6 scenario ผ่าน
  `EXECUTE AS USER` + `sp_set_session_context`
- (ก): throwaway console app (`net10.0`, `Microsoft.Data.SqlClient` 7.0.2 — เดียวกับที่
  `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8 ดึงมาใช้จริง) เปิด connection ด้วย `Max Pool Size=1;Min Pool
  Size=0` บังคับให้ physical connection ตัวเดียวกันถูก reuse แน่นอน (ยืนยันด้วย `ServerProcessId`/SPID ตรงกัน)
  stamp round แรก ปิด แล้วเปิดใหม่รอบสอง อ่านค่าก่อน re-stamp (leak check) แล้ว re-stamp คีย์เดิมซ้ำ (re-stamp ได้จริงไหม)

## ผล — PASS

| ข้อ | กลไกที่ทดสอบ | ผล |
|---|---|---|
| (ก) pooled reuse | SPID round1 vs round2 | เท่ากัน (60=60) — physical connection reuse จริง ไม่ใช่ conn ใหม่ |
| (ก) pooled reuse | ค่าเก่ารั่วข้าม reuse ก่อน re-stamp ไหม | ไม่รั่ว (SqlClient ล้างให้เองผ่าน `sp_reset_connection` ตอน Open รอบสอง) — design ยังคง "stamp เสมอไม่มีเงื่อนไข" ถูกต้อง (ดู Findings ข้อ 1) |
| (ก) pooled reuse | re-stamp คีย์ `@read_only=1` เดิมซ้ำหลัง reuse | สำเร็จ (`RE-STAMP_OK`) — ไม่ throw, ไม่ค้าง read-only lock ข้าม physical connection |
| (ข) cross-schema SCHEMABINDING | `CREATE FUNCTION sec.fn_merchant_predicate ... WITH SCHEMABINDING` อ้าง `admin.PlatformUsers`/`admin.PlatformMerchantAccess` | compile ผ่าน (`OBJECT_ID` คืนค่าไม่ null) |
| (ข) ownership chaining (control) | `spike_app` มี GRANT SELECT เฉพาะ `shop.TestRows` (ไม่มีบน `admin.*`) — query ตรงบน `admin.PlatformUsers` | ถูกปฏิเสธจริง — ยืนยันว่าไม่มี grant หลุดมาช่วย |
| (ข) ownership chaining | `spike_app` query `shop.TestRows` ผ่าน policy ที่ predicate อ้าง `admin.*` ข้าม schema | สำเร็จครบทุก scenario — chaining ทำงานตามคาด (schema ทั้งหมด `AUTHORIZATION dbo`) |
| (ค) tier-based Super | UserId = PlatformUser Tier=1, MerchantId=Guid.Empty | เห็นทุกแถว (2/2) |
| (ค) Scoped assigned | UserId = PlatformUser Tier=0 + มีแถว PMA 1 merchant | เห็นเฉพาะแถวที่ assigned (1/2) |
| (ค) REQ-3.11 fail-closed | UserId = PlatformUser Tier=0 ไม่มีแถว PMA เลย | เห็นศูนย์แถว (0/2) — ไม่ fail-open |
| sentinel guard | MerchantId=Guid.Empty, UserId=NULL | เห็นศูนย์แถว — guard `SESSION_CONTEXT('UserId') IS NOT NULL` ทำงาน |
| no-context | ไม่ stamp อะไรเลย | เห็นศูนย์แถว — deny-all default ยืนยัน |
| merchant branch ปกติ | MerchantId ตรง, UserId=NULL | เห็นเฉพาะแถวของตัวเอง (1/2) — logic เดิมไม่เพี้ยนจาก branch ใหม่ที่เพิ่ม |

output เต็ม: 6/6 scenario ตรงตามคาดทุกตัว, control test permission-denied ตามคาด, catalog verification
(`OBJECT_ID`, `sys.security_policies`) ยืนยันจริงไม่ใช่ print เดา (พลาดรอบแรก — ดู Findings ข้อ 2)

## Findings (ต้อง action)

1. **"stamp เสมอไม่มีเงื่อนไข" (T13) ยังถูกต้อง แม้ observed ว่า SqlClient ล้าง SESSION_CONTEXT ให้เองตอน pooled
   reuse รอบนี้** — design ไม่ได้พึ่งพฤติกรรม reset โดยเจตนาอยู่แล้ว (เหตุผลเดิมใน T13: "โดยไม่พึ่ง reset
   behavior"); ผล spike ยืนยันเพิ่มว่าแม้ reset จะช่วยอยู่แล้วในเคสนี้ ก็ไม่มีเหตุผลให้ผ่อน "stamp เสมอ" เพราะ
   reset เป็น implementation detail ของ pool ไม่ใช่ contract ที่ประกาศ (MARS/connection resiliency/retry อาจ
   ต่างออกไป) — **ไม่ต้องแก้ design**
2. **บั๊กของ spike เอง (ไม่ใช่ของ design):** รอบแรก `PRINT` ติดกับ `CREATE SCHEMA sec` ไม่มี `GO` คั่น → SQL
   Server ปฏิเสธ (ต้องเป็น statement แรกของ batch) → schema `sec` ไม่ถูกสร้าง → ทุก scenario เห็นหมดทุกแถว (false
   positive เพราะ policy ไม่ active) แต่ print "succeeded" ของ spike เองเป็น unconditional เลยไม่ฟ้อง error ให้
   เห็นทันที แก้โดยเติม `GO` ก่อนทุก `CREATE SCHEMA`/`CREATE SECURITY POLICY` + เปลี่ยนจาก print เดาเป็น query
   catalog จริง (`OBJECT_ID`, `sys.security_policies`) แล้วรันซ้ำบน DB สะอาด — บทเรียนสำหรับ task 3: verify
   migration ด้วย catalog query จริงเสมอ อย่าเชื่อ exit code/print เฉย ๆ
3. `.env.integration` (credential ของ `pol-sql` :11434 จริง) ถูก block โดย permission settings ของ agent — spike
   เลี่ยงโดยตั้ง container/password แยกเอง ไม่กระทบ container integration suite ที่รันอยู่แล้ว 12 วัน — task 3/4
   (ตอน cutover จริง) ยังต้องใช้ credential ของจริงตามปกติ ไม่เกี่ยวกับ finding นี้

## Fallback (ไม่ต้องใช้)

spike ผ่านทั้ง 3 ข้อ → ไม่ต้องแก้ design. (ไม่มี fallback เตรียมไว้ตามที่ requirements.md ระบุไว้โดยเจตนา —
"ห้ามด้นสด" ถ้า no-go)

## Cleanup

container `pol-spike-rf1-sql` (port 11435) + scratchpad ของ session (`.sql` script + throwaway console project)
= throwaway, ลบหลังจบ. ไม่มี code เข้า repo.
