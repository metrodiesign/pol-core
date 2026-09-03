# Requirements: Tier 0 Graph Employee Profile

> Status: approved 2026-08-30
> Status-Note: amended 2026-08-30

## Overview

ฟีเจอร์นี้ต่อยอด Tier 0 Microsoft workforce login (`tier-0-microsoft-canonical-email`) ให้ Admin
Console รู้จักพนักงานเป็นตัวตนในองค์กร ไม่ใช่แค่ corporate email โดยหลัง OIDC callback ผ่าน
workforce gate แล้ว ระบบเรียก Microsoft Graph `GET /v1.0/me?$select=employeeId` ด้วย access token
ชั่วคราว นำ `employeeId` ไปค้น HR source แบบ read-only (`dbo.VibEmp`, `dbo.branch`) แล้ว map ชื่อไทย,
สถานที่ปฏิบัติงาน และฝ่าย เข้า `admin.Users` เป็น canonical GUID FK ที่มีอยู่แล้ว (`OfficeId`,
`DivisionId`) พร้อมคอลัมน์ใหม่ `EmployeeId`, `FirstName`, `LastName`

Identity key ของ Tier 0 ยังคงเป็น `(provider=microsoft, subject=canonical-email)` ตาม
`tier-0-microsoft-canonical-email` REQ-3.1 ทุกประการ `employeeId` เป็นข้อมูล profile ที่ผูกกับบัญชี
ไม่ใช่ identity key ข้อกำหนดนี้ supersede เฉพาะ `tier-0-microsoft-canonical-email` REQ-2.28
(ห้ามเรียก Microsoft Graph) ขณะที่ employee profile switch (REQ-12) เปิด — ส่วนที่เหลือของ Tier 0
คงเดิม และขณะ switch ปิด Tier 0 ทำงานตาม spec เดิมทุกข้อ

## Scope

### In

- Tier 0 Admin Microsoft OIDC callback เท่านั้น
- Graph request เดียว: `GET /v1.0/me?$select=employeeId` ด้วย delegated permission `User.Read`
- Admin domain, application, persistence และ EF migration สำหรับ `EmployeeId`, `FirstName`, `LastName`
- read-only lookup บน `dbo.VibEmp` และ `dbo.branch`
- canonical mapping จาก legacy key ไป `cfg.Offices.Id` และ `cfg.Divisions.Id`
- unit, host E2E, SQL integration tests, `.env.example`, OIDC setup และ runbook

### Out

- Tier 1 Merchant Microsoft/Google auth และ UI ใน `pol-admin`
- import, แก้ไข หรือ sync ข้อมูลใน `dbo.VibEmp`/`dbo.branch` และ background HR synchronization
- Graph field อื่นนอกจาก `employeeId`
- เปลี่ยน Tier, role, permission, MerchantAccess จากข้อมูล Microsoft หรือ HR
- ใช้ `employeeId` แทน identity key ปัจจุบัน
- เปลี่ยนชนิด `OfficeId`/`DivisionId`, ลบ FK, ใช้ raw legacy key เป็น FK
- auto-create Office/Division หรือ fallback ไป Office/Division อื่นเมื่อ mapping ไม่พบ
- `PositionId` และ `LevelId` (ไม่ถูกอ่านหรือเขียนโดยฟีเจอร์นี้)

## ข้อเท็จจริงจาก schema และข้อมูล local (2026-08-30)

ตรวจจาก `VCentralPay` บน `pol-db` (:11433) แบบ aggregate เท่านั้น ไม่มี row จริงในเอกสารนี้

| ข้อเท็จจริง | ค่าที่พบ | ผลต่อ requirement |
|---|---|---|
| `dbo.VibEmp.EmpCode` | `nvarchar(100)` NOT NULL, 17,884 แถว distinct ครบ, ยาวสุด 7 ตัว, ไม่มี whitespace | REQ-3 ใช้ exact match หลัง trim ได้ |
| `dbo.VibEmp.FirstNameTh`/`LastNameTh` | `nvarchar(1000)` nullable, ไม่พบค่าว่าง, ยาวสุด 21/19 ตัว | REQ-3.6 รองรับความยาวมากกว่า 500 ได้โดยไม่ตัด |
| `dbo.VibEmp.und_brcode` | `varchar(3)` nullable — ว่าง 180 แถว, ตรง `dbo.branch.br_code` 12,167, ไม่ตรง 5,537 | REQ-4 fail closed จะปิดพนักงานราว 32% ของ source local |
| `dbo.branch.br_code` | `char(3)` NOT NULL, 125 แถว distinct ครบ, 124 แถวเป็นเลข 3 หลัก | REQ-4 ต้องเทียบแบบ trim trailing space ของ `char` |
| `dbo.branch.active_row` | ค่าเป็น `A` 120 แถว และ `C` 5 แถว (ไม่ใช่ Y/N) | ดู Open Question Q4 |
| `dbo.VibEmp.DivisionID` | `nvarchar(100)` nullable — ว่าง 12,169 จาก 17,884 แถว (68%), 563 ค่า distinct | ไม่ใช้ (Q2 ตัดสินแล้ว, REQ-5.13) |
| `dbo.VibEmp.DepartmentID` | 183 ค่า distinct | division source key ตาม REQ-5.1 |
| `dbo.VibEmp.status_code` | `0` 10,927 / `1` 6,520 / `2` 130 / `3` 307 | ไม่ใช้ (Q5 ตัดสินแล้ว, REQ-3.17) |
| `cfg.Offices`/`cfg.Divisions` | seed baseline 8/10 แถว, `Code` ไม่ตรง `br_code`/`DivisionID` แม้แต่แถวเดียว | REQ-4/5 ต้องมี explicit mapping ไม่ใช่ match by code |
| `dbo.VibEmp`/`dbo.branch` ใน repo | ไม่อยู่ใน EF model หรือ migration chain, โหลด one-shot บน local | ดู Open Question Q1 |

## REQ-1: Microsoft Graph employeeId acquisition

**User Story:** As a security owner, I want ระบบดึง `employeeId` จาก Microsoft Graph ด้วย token ชั่วคราวและสิทธิ์ต่ำสุด, so that Tier 0 ได้ตัวตนพนักงานโดยไม่ขยาย attack surface

**Acceptance Criteria (EARS):**

- 1.1 WHERE employee profile switch (REQ-12) เปิด THE SYSTEM SHALL ขอ OIDC scopes `openid email profile User.Read` สำหรับ Admin Microsoft provider
- 1.2 THE SYSTEM SHALL ไม่ขอ Graph permission อื่นนอกจาก `User.Read`
- 1.3 THE SYSTEM SHALL เรียก Graph ด้วย request `GET https://graph.microsoft.com/v1.0/me?$select=employeeId` เท่านั้น
- 1.4 WHEN OIDC code exchange สำเร็จ THE SYSTEM SHALL อ่าน access token จาก callback event ของ framework โดยไม่ตั้ง `SaveTokens=true`
- 1.5 THE SYSTEM SHALL ไม่ persist access token ลง session, cookie, database หรือ audit
- 1.6 THE SYSTEM SHALL ไม่ส่ง access token หรือ Graph response ไปยัง frontend
- 1.7 WHEN framework validate signature, issuer, audience, lifetime, nonce และ state ผ่านแล้ว THE SYSTEM SHALL จึงเรียก Graph
- 1.8 WHEN workforce `tid`/`email` gate ผ่านแล้ว THE SYSTEM SHALL จึงเรียก Graph
- 1.9 THE SYSTEM SHALL เรียก Graph ก่อนเปิด SQL transaction ของ admin resolution
- 1.10 THE SYSTEM SHALL เรียก Graph ก่อน acquire lock `admin-user-identity-mutation`
- 1.11 THE SYSTEM SHALL ไม่เรียก Graph ภายใน SQL transaction ใด
- 1.12 THE SYSTEM SHALL ส่ง Graph request ผ่าน `HttpClient` ที่ตั้ง timeout ไม่เกิน 10 วินาที
- 1.13 THE SYSTEM SHALL parse Graph response ด้วย `System.Text.Json` โดยไม่เพิ่ม package dependency
- 1.14 IF Graph timeout THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unavailable`
- 1.15 IF Graph ตอบ HTTP status ใดที่ไม่ใช่ 200 (รวม 400, 401, 403, 404, 429, 5xx) THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unavailable`
- 1.16 IF Graph response ไม่ใช่ JSON ที่ parse ได้ THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unavailable`
- 1.17 IF Graph response ไม่มี property `employeeId` หรือค่าเป็น null THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-missing`
- 1.18 IF Graph call ล้มเหลวด้วยเหตุใด THEN THE SYSTEM SHALL ไม่ retry request ใน callback เดียวกัน
- 1.19 IF Graph call ล้มเหลวด้วยเหตุใด THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 1.20 IF Graph call ล้มเหลวด้วยเหตุใด THEN THE SYSTEM SHALL ไม่สร้างหรือ bind Admin
- 1.21 IF Graph call ล้มเหลวด้วยเหตุใด THEN THE SYSTEM SHALL เขียน denied-auth audit ตาม flow ปัจจุบันโดยไม่มีข้อมูล Graph
- 1.22 WHEN Graph call ล้มเหลว THE SYSTEM SHALL log เฉพาะ HTTP status class, failure category และ correlation id
- 1.23 THE SYSTEM SHALL ถือ access token ระหว่าง OIDC event กับ Graph call ไว้ใน request state แบบ in-memory เท่านั้น (ไม่อยู่ใน cookie, session store, database หรือ authentication ticket)

## REQ-2: EmployeeId validation และ binding

**User Story:** As a security owner, I want `employeeId` ถูก validate และผูกกับบัญชีแบบ immutable, so that บัญชีหนึ่งแทนพนักงานหนึ่งคนเสมอ

**Acceptance Criteria (EARS):**

- 2.1 WHEN Graph คืน `employeeId` THE SYSTEM SHALL trim whitespace หน้าและท้ายก่อนใช้
- 2.2 IF `employeeId` หลัง trim เป็นค่าว่าง THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-missing`
- 2.3 IF `employeeId` มี control character หรือ whitespace ภายใน THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 2.4 IF `employeeId` หลัง trim ยาวเกิน 16 ตัวอักษร THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 2.5 THE SYSTEM SHALL ใช้ `employeeId` เป็น profile attribute ไม่ใช่ Tier 0 identity key
- 2.6 WHEN Admin ที่ resolve ได้มี `EmployeeId` เป็น NULL THE SYSTEM SHALL bind `EmployeeId` เป็นค่าจาก Graph
- 2.7 WHEN Admin ที่ resolve ได้มี `EmployeeId` ตรงค่า normalized จาก Graph แบบ ordinal THE SYSTEM SHALL resolve ต่อโดยไม่เปลี่ยน `EmployeeId`
- 2.8 IF Admin ที่ resolve ได้มี `EmployeeId` ไม่ตรงค่าจาก Graph THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `identity-conflict`
- 2.9 IF `EmployeeId` ไม่ตรง THEN THE SYSTEM SHALL ไม่ overwrite `EmployeeId` เดิม
- 2.10 IF `employeeId` จาก Graph ถูก bind อยู่กับ Admin รายอื่นแล้ว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `identity-conflict`
- 2.11 THE SYSTEM SHALL enforce uniqueness ของ `admin.Users.EmployeeId` ด้วย filtered unique index บนค่าที่ไม่เป็น NULL โดยใช้ collation default ของ database
- 2.12 IF callback หลายรายการ bind `employeeId` เดียวกันพร้อมกัน THEN THE SYSTEM SHALL bind สำเร็จได้ไม่เกินหนึ่ง Admin
- 2.13 IF unique index race บน `EmployeeId` เกิดขึ้น THEN THE SYSTEM SHALL ปฏิเสธ callback ที่แพ้ด้วย browser reason `identity-conflict`
- 2.14 WHEN `EmployeeId` ถูก bind ครั้งแรก THE SYSTEM SHALL เขียน admin audit action สำหรับ employee bind ใน transaction เดียวกับการ bind
- 2.15 THE SYSTEM SHALL ไม่มี endpoint หรือ command ที่เปลี่ยน `EmployeeId` ที่ bind แล้ว
- 2.16 WHEN `employeeId` ผ่าน trim แล้ว THE SYSTEM SHALL normalize เป็น invariant uppercase ก่อนใช้ในทุก lookup, compare และ persist
- 2.17 WHEN callback ถูกปฏิเสธตาม 2.8, 2.10 หรือ 2.13 THE SYSTEM SHALL เขียน denied-auth audit ด้วย reason ภายใน `employee-mismatch` (2.8) หรือ `employee-taken` (2.10, 2.13) โดย browser reason ยังเป็น `identity-conflict`
- 2.18 WHEN `EmployeeId` ถูก bind ครั้งแรก THE SYSTEM SHALL bump `Version` ของ Admin

## REQ-3: HR employee lookup และ name mapping

**User Story:** As an employee, I want ชื่อไทยของฉันถูกดึงจาก HR source ตรง ๆ, so that Admin Console แสดงตัวตนที่ตรงกับทะเบียนพนักงาน

**Acceptance Criteria (EARS):**

- 3.1 WHEN `employeeId` ผ่าน validation THE SYSTEM SHALL ค้น `dbo.VibEmp` ด้วยเงื่อนไข `EmpCode = @employeeId` แบบ parameterized
- 3.2 THE SYSTEM SHALL เทียบ `EmpCode` กับ normalized `employeeId` (2.16) แบบ equality ตาม collation default ของ database หลัง trim ทั้งสองฝั่ง
- 3.3 THE SYSTEM SHALL ไม่ทำ pattern match, prefix match หรือ padding บน `EmpCode`
- 3.4 IF `dbo.VibEmp` คืน 0 แถว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-missing`
- 3.5 IF `dbo.VibEmp` คืนมากกว่า 1 แถว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 3.6 WHEN พบ VibEmp row เดียว THE SYSTEM SHALL กำหนด `FirstName = Trim(FirstNameTh)`
- 3.7 WHEN พบ VibEmp row เดียว THE SYSTEM SHALL กำหนด `LastName = Trim(LastNameTh)`
- 3.8 THE SYSTEM SHALL เก็บ `FirstName` และ `LastName` เป็น `nvarchar` ความยาวอย่างน้อย 500 โดยไม่ตัดหรือแปลงตัวอักษรไทย
- 3.9 IF `FirstNameTh` เป็น NULL หรือว่างหลัง trim THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 3.10 IF `LastNameTh` เป็น NULL หรือว่างหลัง trim THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 3.11 THE SYSTEM SHALL อ่าน `dbo.VibEmp` แบบ read-only โดยไม่มี INSERT, UPDATE หรือ DELETE ต่อตารางนี้ใน code path ใด
- 3.12 THE SYSTEM SHALL อ่านเฉพาะคอลัมน์ `EmpCode`, `FirstNameTh`, `LastNameTh`, `und_brcode` และ `DepartmentID` จาก `dbo.VibEmp`
- 3.13 WHEN Tier 0 login สำเร็จ THE SYSTEM SHALL refresh `FirstName` และ `LastName` เป็นค่าปัจจุบันจาก VibEmp ทุกครั้ง
- 3.14 WHEN ค่า `EmployeeId`, `FirstName`, `LastName`, `OfficeId` และ `DivisionId` ที่ resolve ได้เท่ากับค่าเดิมทั้งหมด THE SYSTEM SHALL ไม่ bump `Version` ของ Admin
- 3.15 IF `FirstNameTh` หลัง trim ยาวเกิน 500 ตัวอักษร THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 3.16 IF `LastNameTh` หลัง trim ยาวเกิน 500 ตัวอักษร THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 3.17 THE SYSTEM SHALL ไม่ใช้ `dbo.VibEmp.status_code`, `Status` หรือ `TerminatedDate` ในการตัดสิน Tier 0 eligibility
- 3.18 IF HR source query ล้มเหลว (ตารางไม่มี, permission denied หรือ SQL error) THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unavailable`
- 3.19 WHEN callback ถูกปฏิเสธตาม 3.18 THE SYSTEM SHALL เขียน denied-auth audit ด้วย reason ภายใน `hr-source-unavailable`

## REQ-4: Office mapping จาก legacy branch

**User Story:** As an operator, I want สถานที่ปฏิบัติงานของพนักงานถูก resolve เป็น canonical Office, so that `admin.Users.OfficeId` ยังเป็น GUID FK ที่ถูกต้องเสมอ

**Acceptance Criteria (EARS):**

- 4.1 WHEN พบ VibEmp row เดียว THE SYSTEM SHALL อ่าน legacy office source key จาก `dbo.VibEmp.und_brcode`
- 4.2 IF `und_brcode` เป็น NULL หรือว่างหลัง trim THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 4.3 WHEN legacy office source key มีค่า THE SYSTEM SHALL ค้น `dbo.branch` ด้วยเงื่อนไข `br_code = @key` แบบ parameterized
- 4.4 THE SYSTEM SHALL เทียบ `br_code` กับ `und_brcode` แบบ exact หลัง trim trailing space ของ `char(3)`
- 4.5 IF `dbo.branch` คืน 0 แถว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 4.6 IF `dbo.branch` คืนมากกว่า 1 แถว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 4.7 WHEN พบ branch row เดียว THE SYSTEM SHALL resolve `br_code` ไป `cfg.Offices.Id` ผ่าน `cfg.Offices.LegacyKey` (REQ-6) ที่ operator ดูแล
- 4.8 THE SYSTEM SHALL ไม่ resolve Office ด้วยการเทียบ `br_code` กับ `cfg.Offices.Code` หรือ `Name` โดยตรง
- 4.9 IF canonical office mapping ไม่พบ THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 4.10 IF canonical office mapping ชี้ `cfg.Offices.Id` มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 4.11 IF Office ที่ resolve ได้มี Status เป็น Inactive และ Id ไม่เท่ากับ `OfficeId` เดิมของ Admin THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 4.12 THE SYSTEM SHALL ไม่เขียนค่า `br_code` หรือ string ใดลง `admin.Users.OfficeId`
- 4.13 THE SYSTEM SHALL ไม่สร้าง Office ใหม่ระหว่าง Tier 0 login
- 4.14 THE SYSTEM SHALL ไม่ fallback ไป Office อื่นเมื่อ mapping ไม่พบ
- 4.15 THE SYSTEM SHALL อ่าน `dbo.branch` แบบ read-only โดยไม่มี INSERT, UPDATE หรือ DELETE ต่อตารางนี้ใน code path ใด
- 4.16 WHEN Tier 0 login สำเร็จ THE SYSTEM SHALL refresh `OfficeId` เป็นค่าที่ resolve ได้ทุกครั้ง
- 4.17 WHEN Office ที่ resolve ได้มี Status เป็น Inactive และ Id เท่ากับ `OfficeId` เดิมของ Admin THE SYSTEM SHALL คง `OfficeId` เดิมและ resolve ต่อ
- 4.18 THE SYSTEM SHALL ไม่ใช้ `dbo.branch.active_row` ในการตัดสิน office mapping

## REQ-5: Division mapping จาก legacy division key

**User Story:** As an operator, I want ฝ่ายของพนักงานถูก resolve เป็น canonical Division, so that `admin.Users.DivisionId` ยังเป็น GUID FK ที่ถูกต้องเสมอ

**Acceptance Criteria (EARS):**

- 5.1 WHEN พบ VibEmp row เดียว THE SYSTEM SHALL อ่าน legacy division source key จาก `dbo.VibEmp.DepartmentID`
- 5.2 IF legacy division source key เป็น NULL หรือว่างหลัง trim THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 5.3 WHEN legacy division source key มีค่า THE SYSTEM SHALL resolve key ไป `cfg.Divisions.Id` ผ่าน `cfg.Divisions.LegacyKey` (REQ-6) ที่ operator ดูแล
- 5.4 THE SYSTEM SHALL ไม่ resolve Division ด้วยการเทียบ legacy key กับ `cfg.Divisions.Code` หรือ `Name` โดยตรง
- 5.5 IF canonical division mapping ไม่พบ THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 5.6 IF canonical division mapping ชี้ `cfg.Divisions.Id` มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-invalid`
- 5.7 IF Division ที่ resolve ได้มี Status เป็น Inactive และ Id ไม่เท่ากับ `DivisionId` เดิมของ Admin THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `employee-profile-unmapped`
- 5.8 THE SYSTEM SHALL ไม่เขียนค่า legacy division key หรือ string ใดลง `admin.Users.DivisionId`
- 5.9 THE SYSTEM SHALL ไม่สร้าง Division ใหม่ระหว่าง Tier 0 login
- 5.10 THE SYSTEM SHALL ไม่ fallback ไป Division อื่นเมื่อ mapping ไม่พบ
- 5.11 WHEN Tier 0 login สำเร็จ THE SYSTEM SHALL refresh `DivisionId` เป็นค่าที่ resolve ได้ทุกครั้ง
- 5.12 WHEN Division ที่ resolve ได้มี Status เป็น Inactive และ Id เท่ากับ `DivisionId` เดิมของ Admin THE SYSTEM SHALL คง `DivisionId` เดิมและ resolve ต่อ
- 5.13 THE SYSTEM SHALL ไม่อ่าน `dbo.VibEmp.DivisionID` ในการ resolve Division

## REQ-6: Canonical mapping data

**User Story:** As an operator, I want mapping จาก legacy key ไป master-data GUID เป็นข้อมูลที่ตรวจสอบได้, so that การ resolve ไม่พึ่งการเดาและเปลี่ยนได้โดยไม่ deploy code

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL เก็บ canonical mapping เป็นคอลัมน์ `LegacyKey` `nvarchar(100)` NULL บน `cfg.Offices` และ `cfg.Divisions`
- 6.2 THE SYSTEM SHALL บังคับให้ `LegacyKey` หนึ่งค่าชี้ master row ได้ไม่เกินหนึ่งแถวด้วย filtered unique index บนค่าที่ไม่เป็น NULL
- 6.3 THE SYSTEM SHALL ให้ mapping อยู่บน master row เดียวกัน เพื่อให้ legacy key อ้างได้เฉพาะ Office หรือ Division ที่มีอยู่จริง
- 6.4 THE SYSTEM SHALL ไม่ seed `LegacyKey` จากการอนุมานข้อมูล `dbo.VibEmp` หรือ `dbo.branch` ใน migration
- 6.5 THE SYSTEM SHALL ไม่สร้าง Office หรือ Division ใหม่จาก migration ของฟีเจอร์นี้
- 6.6 WHEN `LegacyKey` ถูกเพิ่มหรือแก้ THE SYSTEM SHALL ให้ผลกับ login ครั้งถัดไปโดยไม่ต้อง restart
- 6.7 THE SYSTEM SHALL เทียบ `LegacyKey` กับ legacy source key แบบ equality ตาม collation default ของ database หลัง trim ทั้งสองฝั่ง

## REQ-7: Atomic profile persistence

**User Story:** As a security owner, I want ทุก field ของ profile commit พร้อมกันหรือไม่ commit เลย, so that ไม่มี Admin หรือ session ที่มี profile ครึ่งเดียว

**Acceptance Criteria (EARS):**

- 7.1 THE SYSTEM SHALL resolve employee, name, office และ division ให้ครบก่อน commit Admin mutation
- 7.2 THE SYSTEM SHALL commit `EmployeeId`, `FirstName`, `LastName`, `OfficeId` และ `DivisionId` ใน transaction เดียวกับ Admin bind หรือ JIT create
- 7.3 THE SYSTEM SHALL commit profile mutation ภายใต้ lock `admin-user-identity-mutation` เดียวกับ identity mutation ปัจจุบัน
- 7.4 IF field ใด field หนึ่ง resolve ไม่ได้ THEN THE SYSTEM SHALL rollback ทั้ง transaction
- 7.5 IF profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง JIT Admin
- 7.6 IF profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่ bind Microsoft subject เข้า Admin เดิม
- 7.7 IF profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 7.8 IF profile resolution ล้มเหลว THEN THE SYSTEM SHALL ไม่เขียน login-success audit
- 7.9 IF profile resolution ล้มเหลว THEN THE SYSTEM SHALL เขียน denied-auth audit บน fresh scope ตาม flow ปัจจุบัน
- 7.10 THE SYSTEM SHALL ทำ SQL lookup ทุกรายการของ REQ-3, REQ-4, REQ-5 แบบ parameterized
- 7.11 WHEN profile ของ Admin เดิมเปลี่ยนค่า THE SYSTEM SHALL bump `Version` ของ Admin
- 7.12 WHEN profile ของ Admin เดิมเปลี่ยนค่า THE SYSTEM SHALL ไม่ bump `AuthorizationVersion`
- 7.13 WHEN profile ของ Admin เดิมเปลี่ยนค่า THE SYSTEM SHALL stamp `UpdatedAt` ใน transaction เดียวกัน
- 7.14 THE SYSTEM SHALL ประเมิน outcome เดิมของ Tier 0 (email identity conflict, Suspended, not-provisioned) ก่อน HR lookup ตาม REQ-3, 4, 5
- 7.15 IF outcome เดิมของ Tier 0 ไม่ใช่ resolvable THEN THE SYSTEM SHALL ไม่อ่าน `dbo.VibEmp`, `dbo.branch`, `cfg.Offices` หรือ `cfg.Divisions` สำหรับ callback นั้น
- 7.16 THE SYSTEM SHALL ทำ HR lookup และ mapping resolution ภายใน transaction และ lock เดียวกับ Admin mutation
- 7.17 WHEN HR lookup และ mapping สำเร็จ THE SYSTEM SHALL ประเมิน `EmployeeId` bind หรือ mismatch (REQ-2.6-2.10) ก่อน commit

## REQ-8: Schema migration

**User Story:** As an operator, I want migration เพิ่มคอลัมน์ profile โดยไม่แตะ HR source และ rollback ได้, so that deploy และถอยกลับปลอดภัย

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL เพิ่มคอลัมน์ `admin.Users.EmployeeId` เป็น `nvarchar(16)` NULL
- 8.2 THE SYSTEM SHALL เพิ่มคอลัมน์ `admin.Users.FirstName` เป็น `nvarchar(500)` NULL
- 8.3 THE SYSTEM SHALL เพิ่มคอลัมน์ `admin.Users.LastName` เป็น `nvarchar(500)` NULL
- 8.4 THE SYSTEM SHALL สร้าง unique index บน `admin.Users.EmployeeId` ที่มี filter `[EmployeeId] IS NOT NULL`
- 8.5 THE SYSTEM SHALL คง `admin.Users.OfficeId` เป็น `uniqueidentifier` FK ไป `cfg.Offices.Id`
- 8.6 THE SYSTEM SHALL คง `admin.Users.DivisionId` เป็น `uniqueidentifier` FK ไป `cfg.Divisions.Id`
- 8.7 THE SYSTEM SHALL ไม่สร้าง, แก้ หรือลบ `dbo.VibEmp` และ `dbo.branch` ใน migration ใด
- 8.8 THE SYSTEM SHALL ไม่ backfill `EmployeeId`, `FirstName`, `LastName` ของ row เดิมใน migration
- 8.9 WHEN migration `Down()` ทำงาน THE SYSTEM SHALL ลบเฉพาะคอลัมน์และ index ที่ฟีเจอร์นี้เพิ่ม (`admin.Users` 3 คอลัมน์, `LegacyKey` 2 คอลัมน์) โดยไม่แตะข้อมูลอื่นของ `dbo.VibEmp`, `dbo.branch`, `cfg.Offices`, `cfg.Divisions`
- 8.10 THE SYSTEM SHALL ใช้ migration script ตามกฎ `check-migration-script.sh --write` ของ repo
- 8.11 THE SYSTEM SHALL grant สิทธิ์ SELECT บน `dbo.VibEmp` และ `dbo.branch` ให้ principal `pol_app` แบบ explicit เมื่อตารางมีอยู่
- 8.12 IF `dbo.VibEmp` หรือ `dbo.branch` ไม่มีอยู่ขณะ migration THEN THE SYSTEM SHALL ข้าม grant ของตารางนั้นโดยไม่ทำให้ migration ล้ม
- 8.13 THE SYSTEM SHALL เพิ่มคอลัมน์ `cfg.Offices.LegacyKey` และ `cfg.Divisions.LegacyKey` พร้อม filtered unique index ใน migration เดียวกับ 8.1-8.4

## REQ-9: Privacy และ logging

**User Story:** As a compliance owner, I want ข้อมูลพนักงานและ token ไม่หลุดลง log หรือ browser, so that ระบบเป็นไปตาม PII policy

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL ไม่ log access token
- 9.2 THE SYSTEM SHALL ไม่ log `employeeId`
- 9.3 THE SYSTEM SHALL ไม่ log email
- 9.4 THE SYSTEM SHALL ไม่ log `FirstName` หรือ `LastName`
- 9.5 THE SYSTEM SHALL ไม่ log legacy office key หรือ legacy division key
- 9.6 THE SYSTEM SHALL ไม่ log Graph response body
- 9.7 THE SYSTEM SHALL ไม่ใส่ `employeeId`, ชื่อ, legacy key หรือ Graph response ใน browser reason หรือ query string
- 9.8 THE SYSTEM SHALL ไม่ใส่ `employeeId`, ชื่อ หรือ legacy key ใน `AuthAudits` หรือ `UserAudits`
- 9.9 THE SYSTEM SHALL ไม่ใช้ `employeeId`, email หรือชื่อจริงของพนักงานเป็น test fixture
- 9.10 THE SYSTEM SHALL ผ่าน secret scan และ PII log scan ของ repo gate โดยไม่มี finding ใหม่

## REQ-10: Regression ของ Tier 0 behavior เดิม

**User Story:** As an existing admin, I want การ login เดิมยังทำงานเหมือนเดิม, so that ฟีเจอร์นี้ไม่เปลี่ยนสิทธิ์หรือ identity ที่มีอยู่

**Acceptance Criteria (EARS):**

- 10.1 WHEN Admin เดิมที่มี exact Microsoft identity และ profile resolve ครบ login THE SYSTEM SHALL สร้าง session ตาม flow ปัจจุบัน
- 10.2 IF Admin ที่ resolve ได้เป็น Suspended THEN THE SYSTEM SHALL ปฏิเสธด้วย browser reason `suspended` ก่อน profile mutation
- 10.3 IF identity conflict ตาม `tier-0-microsoft-canonical-email` REQ-4/5 เกิดขึ้น THEN THE SYSTEM SHALL ปฏิเสธด้วย browser reason `identity-conflict` เหมือนเดิม
- 10.4 THE SYSTEM SHALL คง bind และ JIT semantics ของ `tier-0-microsoft-canonical-email` REQ-4
- 10.5 THE SYSTEM SHALL คง recovery path หลัง unique constraint race ของ `tier-0-microsoft-canonical-email` REQ-5.8-5.9
- 10.6 THE SYSTEM SHALL ไม่เปลี่ยน Tier, Role, Permission หรือ MerchantAccess จากข้อมูล Graph หรือ HR
- 10.7 THE SYSTEM SHALL ไม่เปลี่ยน `PositionId` หรือ `LevelId` ระหว่าง Tier 0 login
- 10.8 THE SYSTEM SHALL ไม่เปลี่ยน Tier 1 Merchant authentication path
- 10.9 THE SYSTEM SHALL ไม่เปลี่ยน authorization version rules ของ `rls-to-query-filter` REQ-4.11
- 10.10 THE SYSTEM SHALL คงพฤติกรรม Google provider และ invited-account bind ที่ไม่ใช่ Tier 0 โดยไม่เรียก Graph หรือ HR lookup
- 10.11 THE SYSTEM SHALL คง `UpdateProfile` endpoint เดิมที่รับ `PositionId`, `OfficeId`, `LevelId`, `DivisionId` โดยไม่เปลี่ยน contract
- 10.12 WHEN Tier 0 login สำเร็จ THE SYSTEM SHALL ให้ค่า `OfficeId` และ `DivisionId` จาก HR ทับค่าที่เคยตั้งผ่าน `UpdateProfile`
- 10.13 WHILE employee profile switch (REQ-12) ปิด THE SYSTEM SHALL คง Tier 0 flow เดิมทุกประการตาม `tier-0-microsoft-canonical-email`

## REQ-11: Configuration และ operations

**User Story:** As an operator, I want config, consent และ runbook ครบก่อนเปิดใช้, so that deploy ไม่ล้มเพราะ permission หรือ env ขาด

**Acceptance Criteria (EARS):**

- 11.1 THE SYSTEM SHALL อ่าน Graph base URL จาก configuration โดยมี default `https://graph.microsoft.com`
- 11.2 THE SYSTEM SHALL ระบุ config key ใหม่ทุกตัวใน `.env.example` ด้วยค่า placeholder
- 11.3 THE SYSTEM SHALL ระบุขั้นตอน grant `User.Read` และ admin consent ใน runbook OIDC setup
- 11.4 THE SYSTEM SHALL ระบุขั้นตอนเตรียม mapping data ของ office และ division ใน runbook
- 11.5 THE SYSTEM SHALL ระบุ deployment order และ rollback plan ใน runbook
- 11.6 WHERE test host ตั้ง `HttpClient` handler ทดแทนสำหรับ Graph THE SYSTEM SHALL ใช้ handler นั้นโดยไม่แตะ network จริง
- 11.7 THE SYSTEM SHALL ให้ integration และ host E2E test fixture สร้าง `dbo.VibEmp` และ `dbo.branch` ด้วย DDL ขั้นต่ำ (เฉพาะคอลัมน์ตาม 3.12 และ `br_code`) พร้อมข้อมูลปลอมเมื่อตารางยังไม่มี
- 11.8 THE SYSTEM SHALL ระบุขั้นตอน operator ปลด `EmployeeId` จาก Admin เดิม (กรณีพนักงานเปลี่ยน email) เป็น SQL script ที่ user รันเองใน runbook

## REQ-12: Employee profile switch

**User Story:** As an operator, I want เปิดใช้ employee profile ได้หลัง mapping และ consent พร้อม, so that deploy ไม่ล็อก Admin ทุกรายทันทีและ rollback ไม่ต้อง revert migration

**Acceptance Criteria (EARS):**

- 12.1 THE SYSTEM SHALL อ่าน switch จาก configuration key `AdminAuth:Providers:Microsoft:RequireEmployeeProfile` โดยมี default `false`
- 12.2 WHILE switch ปิด THE SYSTEM SHALL ขอ OIDC scopes `openid email profile` เท่านั้น
- 12.3 WHILE switch ปิด THE SYSTEM SHALL ไม่เรียก Microsoft Graph
- 12.4 WHILE switch ปิด THE SYSTEM SHALL ไม่อ่าน `dbo.VibEmp` หรือ `dbo.branch`
- 12.5 WHILE switch ปิด THE SYSTEM SHALL ไม่เปลี่ยน `EmployeeId`, `FirstName`, `LastName`, `OfficeId`, `DivisionId` ระหว่าง Tier 0 login
- 12.6 WHILE switch เปิด THE SYSTEM SHALL บังคับ REQ-1 ถึง REQ-7 ทุกข้อ
- 12.7 THE SYSTEM SHALL อ่านค่า switch ตอน boot และไม่เปลี่ยนระหว่าง process ทำงาน
- 12.8 WHEN switch เปิดใน Production และ Microsoft provider ไม่ได้ config THE SYSTEM SHALL ล้ม boot ด้วย diagnostic ตาม boot guard ปัจจุบัน

## Edge Cases & Open Questions

### Assumptions ที่ต้องยืนยัน

| ID | Assumption | Alternative | ผลถ้าเปลี่ยน |
|---|---|---|---|
| A1 | Graph `/me.employeeId` = `dbo.VibEmp.EmpCode` หลัง trim | Entra custom claim หรือ mapping key อื่น | REQ-1, REQ-3 |
| A2 | `EmployeeId` bind ครั้งแรกแล้ว immutable, ค่าอื่นภายหลัง = identity-conflict | approved employee-transfer workflow | REQ-2.6-2.15 |
| A3 | `FirstName`, `LastName`, `OfficeId`, `DivisionId` refresh ทุก successful login | bind ครั้งแรก หรือ background sync | REQ-3.13, 4.16, 5.11 |
| A4 | ทุก resolution failure fail closed ก่อนสร้าง session/JIT | login ต่อด้วย nullable profile | REQ-1, 3, 4, 5, 7 |
| A5 | `employeeId` ยาวไม่เกิน 16 ตัวตาม Microsoft Graph user contract | ค่าอื่นที่ Entra อนุญาต | REQ-2.4, 8.1 |
| A6 | canonical mapping เก็บเป็นคอลัมน์ `LegacyKey` บน `cfg.Offices`/`cfg.Divisions` ที่ operator เติมเอง (ตัดสินแล้ว F3) | mapping table แยก | REQ-6, 8.9, 8.13 |
| A7 | refresh ทุก login overwrite `OfficeId`/`DivisionId` ที่ Super เคยตั้งผ่าน `UpdateProfile` (ตัดสินแล้ว F4) | HR เป็น source เฉพาะเมื่อ field ยังว่าง | REQ-4.16, 5.11, 10.12 |
| A8 | access token อ่านได้จาก `OnTokenValidated` (`TokenEndpointResponse`) เท่านั้น; เรียก Graph ภายใน event นั้นโดยตรง ไม่ stash token ที่ใด (ยืนยันกับ package 10.0.8 แล้ว) | stash ผ่าน `HttpContext.Items` | REQ-1.4, 1.23 |
| A9 | production มี `dbo.VibEmp` และ `dbo.branch` schema เดียวกับ local ผ่าน replication ที่ ops ดูแล (Q1 ยังไม่มีคำตอบ) | ETL/linked server | REQ-8.11-8.12, 11.5 |

### Open Questions

ทุกข้อตัดสินแล้ว (2026-08-30) ยกเว้น Q1 ที่บันทึกเป็น assumption A9 รอ ops ยืนยัน

| ID | คำถาม | คำตัดสิน | ผลในไฟล์ |
|---|---|---|---|
| Q1 | HR source บน production มาจากไหน | ยังไม่มีคำตอบ — ใช้ A9 | REQ-8.12 ข้าม grant เมื่อตารางไม่มี, REQ-12 ป้องกันล็อก |
| Q2 | Division source key ใช้ `DivisionID` หรือ `DepartmentID` | `DepartmentID` (`DivisionID` ว่าง 68%, session 2026-08-29 ตัดสินไว้แล้ว) | REQ-3.12, 5.1, 5.13 |
| Q3 | Office source key คง `und_brcode` → `dbo.branch` | คงตาม prompt, บันทึก 32% no-match เป็น known limitation | REQ-4.1, ตาราง facts |
| Q4 | เช็ค `dbo.branch.active_row` | ไม่เช็ค — `cfg.Offices.Status` เป็น canonical | REQ-4.18 |
| Q5 | ปฏิเสธพนักงานพ้นสภาพที่ชั้นนี้ | ไม่ — Entra account disable เป็น gate | REQ-3.17 |
| Q6 | browser reason ใหม่ 4 ค่า | คงทั้ง 4 | REQ-1, 2, 3, 4, 5 |
| Q7 | Graph timeout | 10 วินาที | REQ-1.12 |

### Findings log (spec-analyze 2026-08-30, anchor `501b1ed` — ไฟล์ยังไม่ commit ใช้ HEAD)

| ID | หมวด | Finding | คำตัดสิน | ผลในไฟล์ |
|---|---|---|---|---|
| F1 | inconsistency | Inactive fail closed ขัด semantic "existing account referenceable" | a: fail เฉพาะเมื่อค่าเปลี่ยน | 4.11, 4.17, 5.7, 5.12 |
| F2 | inconsistency | ordinal compare vs collation CI ของ index/`EmpCode` | a: normalize uppercase invariant + collation default | 2.7, 2.11, 2.16, 3.2, 6.7 |
| F3 | inconsistency | 8.9/8.12 สมมติ mapping table แต่ A6 ยังเปิด | b: column `LegacyKey` บน master | REQ-6, 4.7, 5.3, 8.9, 8.13 |
| F4 | inconsistency | 10.11 อ่านได้ว่าตัด field ของ endpoint | a: endpoint คงเดิม, HR ทับ | 10.11, 10.12 |
| F5 | ambiguity | ลำดับ precedence ของ failure และตำแหน่ง HR lookup | a: outcome เดิมก่อน, HR ใน tx ใต้ lock | 7.14-7.17 |
| F6 | ambiguity | `identity-conflict` ปนกับ email conflict | b: browser เดิม, audit reason ภายในแยก | 2.17 |
| F7 | ambiguity | 11.6 ผูก Development environment | a: test host handler | 11.6 |
| F8 | ambiguity | whitespace ภายใน `employeeId` | a: reject | 2.3 |
| F9 | conflict | deploy แล้ว Tier 0 ล็อกทุกรายจน mapping พร้อม | a: switch default `false` | REQ-12, 1.1, 10.13 |
| F10 | conflict | ชื่อยาวเกิน 500 ไม่มี behavior | a: fail `employee-profile-invalid` | 3.15, 3.16 |
| F11 | gap | Graph 400/404/2xx อื่นไม่ครอบ | a: ทุก status ที่ไม่ใช่ 200 | 1.15 |
| F12 | gap | test ไม่มี `dbo.VibEmp`/`dbo.branch`, grant เมื่อตารางไม่มี | a: fixture DDL ขั้นต่ำ + skip grant | 8.12, 11.7 |
| F13 | gap | employee เปลี่ยน email แล้ว `employeeId` ถูก bind ล็อกถาวร | a: runbook SQL script operator รันเอง | 11.8 |
| F14 | gap | bind `EmployeeId` ไม่ bump Version, 9.8 ไม่ครอบ `UserAudits` | a: เพิ่มทั้งสาม | 2.18, 3.14, 9.8 |
| A8 | assumption | access token อยู่เฉพาะ `OnTokenValidated` | บันทึกเป็น assumption | A8, 1.23 |

### Findings log (spec-architect critique ของ design.md 2026-08-30)

| ID | severity | Finding | คำตัดสิน | ผลในไฟล์ |
|---|---|---|---|---|
| B1 | BLOCKING | ไม่มี error path เมื่อ HR table ไม่มี/ไม่มีสิทธิ์ → หลุดเป็น `resolve-failed` | เพิ่มเกณฑ์ | 3.18, 3.19 |
| B2 | BLOCKING | `SqlQuery<T>` ไม่ตรง regex ของ `BypassPrimitiveTests` | design ใช้ `SqlQueryRaw` + `SqlParameter` | design |
| M1 | MAJOR | `ExecuteInTransactionAsync` commit เมื่อ return; staged entity ค้าง | design ใช้ typed exception ให้เข้า rollback + `ChangeTracker.Clear()` path | design (7.4 คงเดิม) |
| M2 | MAJOR | recovery path ไม่ apply profile และ audit reason ผิด | design: switch เปิด → re-run transaction 1 ครั้ง, ยังชน → `employee-taken` | design |
| M3 | MAJOR | Graph reader ไม่มี logger/correlation id | design เพิ่ม | design |
| M4 | MAJOR | ไฟล์ Graph reader อยู่นอก gate `Tier0_catch_paths_never_pass_exception_objects_to_logger` | design เพิ่มไฟล์เข้า gate | design |
| M5 | MAJOR | `LoginService` default arm กลืน outcome ใหม่ | design บังคับ exhaustive switch | design |
| M6 | MAJOR | fixture ไม่ GRANT และ dev DB มีตารางจริงพร้อม PII | design: GRANT หลัง CREATE, scope ด้วย test key, ลบ row ตัวเอง | design |
| M7 | MAJOR | `cfg.Offices`/`cfg.Divisions` เป็น EF entity อยู่แล้ว | design ใช้ LINQ แทน raw SQL 2 ตัว | design |
| N1 | MINOR | 2.15 ไม่มี element บังคับ | design: private setter + static gate ห้าม assignment นอก `ApplyEmployeeProfile` | design |
| N2 | MINOR | LegacyKey 2 แถวทดสอบ integration ไม่ได้เพราะ unique index | design ระบุเป็น unit-level | design |
| N3 | MINOR | `assert-fresh-db.sql` แก้ 3 จุด | design ระบุ | design |
| N4 | MINOR | test double ที่ต้องแก้ไม่ถูกลิสต์ | design เพิ่ม | design |
| N5 | MINOR | `GetByEmployeeIdAsync` ต้องยกเว้นตัวเอง | design ระบุ | design |
| N6 | MINOR | A8 ขัดกับ design | แก้ A8 | A8 |
| V1 | verify | collation กับ `EmpCode` มีตัวอักษร | ตรวจแล้ว: `Thai_100_CI_AS`, `EmpCode` ที่มีตัวอักษร = 0 แถว → normalize uppercase ปลอดภัย | ตาราง facts |

### Edge cases ที่ครอบแล้ว

- Graph คืน `employeeId` เป็น empty string, whitespace, control char หรือยาวเกิน (REQ-2.2-2.4)
- EmpCode ตรงหลายแถว, branch ตรงหลายแถว, mapping ชี้หลาย GUID (REQ-3.5, 4.6, 4.10, 5.6)
- `char(3)` trailing space ของ `br_code` เทียบกับ `varchar(3)` (REQ-4.4)
- สอง callback แข่ง bind `employeeId` เดียวกัน (REQ-2.12-2.13)
- Admin เดิมมี `EmployeeId` แล้วแต่ Graph ส่งค่าอื่น (REQ-2.8-2.9)
- Office/Division ถูก deactivate หลัง mapping สร้างแล้ว (REQ-4.11, 5.7)
- profile เท่าเดิมทุก field ต้องไม่ bump `Version` (REQ-3.14)
- Suspended ต้องถูกปฏิเสธก่อนแตะ profile (REQ-10.2, 7.14-7.15)
- Office/Division ที่ inactive แต่เป็นค่าเดิมของ Admin ต้องผ่าน (REQ-4.17, 5.12)
- ชื่อไทยยาวเกิน 500 หลัง trim (REQ-3.15-3.16)
- switch ปิด = flow เดิม ไม่ขอ `User.Read` ไม่เรียก Graph (REQ-12)

### Known limitations

- ข้อมูล local: `und_brcode` ไม่ตรง `dbo.branch` 5,537 แถว + ว่าง 180 แถว (32%) พนักงานกลุ่มนี้ login ไม่ได้
  จนกว่า HR source แก้ข้อมูล (Q3 ตัดสินคงตาม prompt)
- `LegacyKey` ต้องถูกเติมโดย operator ก่อนเปิด switch ไม่มี seed อัตโนมัติ (REQ-6.4)
