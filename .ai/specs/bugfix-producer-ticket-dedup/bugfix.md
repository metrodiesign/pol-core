# Bugfix: Producer registration ออก RegistrationTicket ซ้ำสำหรับ subject/email เดิม
> Status: approved 2026-06-30

## Current Behavior (Defect)

WHEN ผู้ใช้ Google คนเดิม (subject เดียวกัน) ผ่าน OIDC callback ที่ resolve เป็น
`NotFound` (Registration) หรือ `Rejected` (Correction) ซ้ำมากกว่าหนึ่งครั้งภายในอายุ
ticket THEN ระบบ insert row ใหม่ลง `RegistrationTickets` ทุกครั้งแบบ unconditional —
ไม่มี uniqueness check ใด ๆ ก่อน insert

Repro:
1. Login Google ด้วย account ที่ยังไม่เคยลงทะเบียน -> callback resolve `NotFound`
   -> `IssueTicketAndRedirectAsync` (`src/Hosts/Api/ProducerLoginService.cs:172-174`)
   ออก ticket row #1 (`UsedAt IS NULL`, ยังไม่ expire)
2. Login Google ด้วย account เดิมอีกครั้งก่อน ticket #1 หมดอายุ
3. Observed: เกิด ticket row #2 (subject/email ตรงกับ #1) — table โตทุกรอบ
   (ข้อมูลจริง: 4 rows subject `115307079748731734469` / email `metrodiesign@gmail.com`)

## Expected Behavior

- F1  WHEN callback จะออก ticket และมี pending ticket (`UsedAt IS NULL` AND `ExpiresAt > now`)
      ที่ `Subject` ตรง OR `Email` ตรงอยู่แล้ว THE SYSTEM SHALL ไม่ insert ticket row ใหม่
      และไม่เปิด session
- F2  WHEN block ตาม F1 THE SYSTEM SHALL redirect (302) ไป SPA error page พร้อม
      `reason=registration-pending` (query string) เพื่อให้ FE อ่าน reason แล้ว render
      ข้อความได้ — รูปแบบเดียวกับ `DenyAsync` (`ErrorPath?reason=...`)

## Unchanged Behavior

- B1  WHEN ไม่มี pending ticket สำหรับ subject/email และ outcome เป็น `NotFound`
      THE SYSTEM SHALL CONTINUE TO ออก Registration ticket แล้ว redirect ไป register page
- B2  WHEN ไม่มี pending ticket และ outcome เป็น `Rejected`
      THE SYSTEM SHALL CONTINUE TO ออก Correction ticket แล้ว redirect ไป register page
- B3  WHEN ticket เดิมของ subject/email หมดอายุแล้ว (`ExpiresAt <= now`, ยังไม่ถูก consume)
      THE SYSTEM SHALL CONTINUE TO ถือว่าไม่ใช่ pending และยอมออก ticket ใหม่ได้
- B4  WHEN outcome เป็น `PendingApproval`
      THE SYSTEM SHALL CONTINUE TO ตอบ 403 awaiting-approval โดยไม่ออก ticket
- B5  WHEN submit ฟอร์มด้วย subject ที่มี `ProducerAccount` อยู่แล้ว
      THE SYSTEM SHALL CONTINUE TO แปลง unique-violation เป็น 409 ผ่าน unit of work
- B6  WHEN ticket ถูก consume ไปแล้ว (`UsedAt` ถูก set)
      THE SYSTEM SHALL CONTINUE TO ปฏิเสธการ replay ticket เดิม
