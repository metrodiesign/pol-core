# Requirements: Admin Employee Profile Sync

> Status: approved 2026-09-03
> Status-Note: amended and approved 2026-09-03 — employee profile is mandatory on every new Admin Microsoft OIDC callback
> Workflow: Requirements-First

เอกสารนี้กำหนด flow `Entra → employeeId → dbo.VibEmp → admin.Users` สำหรับ Admin Microsoft login โดยคง authentication identity `(Provider, TenantId, Subject)` แยกจาก HR profile และลด HR mapping ให้เหลือเฉพาะรหัสพนักงานกับชื่อเท่านั้น

## Overview

ทุก Admin Microsoft OIDC authorization/callback ใหม่ต้องขอ delegated `User.Read` และหลัง Microsoft OIDC validationกับ workforce tenant validationสำเร็จ ระบบอ่าน `employeeId` จาก Microsoft Graphหนึ่งครั้ง ใช้ policyเดิม normalizeค่า แล้วค้น `[dbo].[VibEmp]` ด้วย `EmpCode` แบบ exact parameterized matchก่อนบันทึก `EmployeeId`, `FirstName`, `LastName`อย่าง atomicกับ identity/JIT/audit mutationเดิม Requestจาก Admin sessionที่มีอยู่และ session rotationไม่เรียก Graph

ฟีเจอร์นี้ supersede เฉพาะส่วน employee-profile ของ `tier0-graph-employee-profile` ที่อ่าน `dbo.branch`, `und_brcode`, `DepartmentID`, Office หรือ Division ส่วน tenant-aware identity ของ `tier0-microsoft-tenant-aware-identity`, RBAC, Tier, roles, permissions, `MerchantAccess`, session security และ Merchant-user authentication คงเดิม

## Scope และ baseline จาก filesystem

### In scope

- Admin Microsoft OIDC callback และ Microsoft Graph `GET /v1.0/me?$select=employeeId`
- HR lookup แบบ read-only เฉพาะ `EmpCode`, `FirstNameTh`, `LastNameTh` จาก `[dbo].[VibEmp]`
- atomic bind/refresh ของ `admin.Users.EmployeeId`, `FirstName`, `LastName`
- existing Admin และ Active/Scoped/roleless JIT Admin
- schema/index verification, migration compatibility, synthetic tests และ operator runbook

### Out of scope

- `employeeId` เป็น login identity หรือ authorization key
- email, UPN, `preferred_username` หรือ `WorkforceEmailKey` เป็น auth fallback หรือ HR lookup key
- `DepartmentID`, branch, Office, Division, Position, Level หรือ employment status
- RBAC, Tier, roles, permissions, `MerchantAccess` และ Merchant-user authentication
- write, seed หรือ production schema ownership ของ `[dbo].[VibEmp]`
- dependency ใหม่, deploy, production query, commit, push หรือ PR

### Conflict inventory

| เรื่อง | Filesystem ปัจจุบัน | Requirement ใหม่ |
|---|---|---|
| Microsoft identity | ใช้ `(microsoft, TenantId=tid, Subject=oid)` แล้ว | คงเดิม ห้ามใช้ HR/email แทน |
| HR schema | production caller ใช้ `dbo.VibEmp`; ไม่พบ `cfg.VibEmp` | pin `dbo.VibEmp` เพียง schema เดียว |
| HR projection | `EmployeeProfileReader` ยังอ่าน `und_brcode`, `DepartmentID`, `dbo.branch`, Offices และ Divisions | ตัด production decision path เหลือ 3 source columns |
| Target schema | `EmployeeId nvarchar(16) NULL`, ชื่อ `nvarchar(500) NULL`, global filtered unique index มีแล้ว | verify และ reuse shape เดิม; ห้ามสร้าง DDL ซ้ำโดยไม่มีเหตุผล |
| HR ownership | `dbo.VibEmp` ไม่อยู่ใน EF model/migration และ grant เป็น conditional `SELECT` | คง external/operator-managed read-only |

## REQ-1: Validated Entra identity และ Graph acquisition

**User Story:** As a security owner, I want อ่าน `employeeId` หลังตรวจ Microsoft identity ครบและใช้ token ชั่วคราวเท่านั้น, so that HR profile data ไม่ลดความแข็งแรงของ authentication

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL ใช้ `(Provider=microsoft, TenantId=validated tid, Subject=validated oid)` เป็น Microsoft Admin authentication identity
- 1.2 THE SYSTEM SHALL ถือ `EmployeeId` เป็น HR profile keyที่ไม่เป็นส่วนของ authentication identity
- 1.3 WHEN OIDC framework validate signature, issuer, audience, nonce, lifetime และ state สำเร็จ THE SYSTEM SHALL จึงอ่าน workforce claims
- 1.4 WHEN validated `tid` และ `oid` ผ่าน tenant-aware workforce policy THE SYSTEM SHALL จึงขอ employee profile
- 1.5 WHEN Admin Microsoft OIDC authorizationเริ่ม THE SYSTEM SHALL ขอ scopes `openid email profile User.Read`
- 1.6 WHEN Admin Microsoft OIDC callbackใหม่ผ่าน protocolและ workforce validation THE SYSTEM SHALL เรียก `GET {GraphBaseUrl}/v1.0/me?$select=employeeId` หนึ่งครั้ง
- 1.7 THE SYSTEM SHALL ไม่ retry Graph request ใน callback เดียวกัน
- 1.8 THE SYSTEM SHALL ใช้ Graph `employeeId` จาก request ปัจจุบันเท่านั้น
- 1.9 THE SYSTEM SHALL ไม่ persist Graph access token ใน cookie, session, authentication ticket, database หรือ audit
- 1.10 THE SYSTEM SHALL ไม่ log Graph access token
- 1.11 THE SYSTEM SHALL ไม่ log Graph response body
- 1.12 THE SYSTEM SHALL ตั้ง Graph request timeout ไม่เกิน 10 วินาที
- 1.13 THE SYSTEM SHALL เรียก Graph ก่อนเปิด Admin SQL transaction
- 1.14 IF OIDC protocol validation ล้มเหลว THEN THE SYSTEM SHALL ไม่เรียก Graph
- 1.15 IF workforce tenant validation ล้มเหลว THEN THE SYSTEM SHALL ไม่เรียก Graph
- 1.16 IF Graph timeout, transport failure หรือ status ไม่ใช่ 200 THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 1.17 IF Graph response parse ไม่ได้ THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 1.18 IF Graph response ไม่มี `employeeId` หรือค่าเป็น null THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-missing`
- 1.19 IF Graph acquisition ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 1.20 WHILE environment เป็น Production THE SYSTEM SHALL pin `GraphBaseUrl` เป็น `https://graph.microsoft.com`
- 1.21 WHERE test host แทน Graph transport THE SYSTEM SHALL อนุญาต replacement HTTP handler หรือ non-production base URL โดยไม่แตะ networkจริง
- 1.22 IF Graph acquisition ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้างหรือ mutate Admin identityหรือ profile
- 1.23 IF Graph acquisition ล้มเหลว THEN THE SYSTEM SHALL ไม่เขียน login-success, JIT, employee-bind หรือ employee-profile-sync audit
- 1.24 WHEN request authenticateด้วย Admin sessionที่มีอยู่หรือ session rotation THE SYSTEM SHALL ไม่เรียก Microsoft Graph
- 1.25 IF OIDC token exchangeไม่มี access token THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 1.26 IF Graphคืน HTTP `401`หรือ`403` THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 1.27 IF callback protocol messageมี exact provider error code `consent_required` THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 1.28 IF userยกเลิก loginและ frameworkส่ง user-cancel signal `access_denied` THEN THE SYSTEM SHALL คง browser reason `access-denied`
- 1.29 THE SYSTEM SHALL ไม่ parseหรือ expose `error_description`, AADSTS message, exception messageหรือ provider detailเพื่อ classify consent failure
- 1.30 THE SYSTEM SHALL ไม่มี runtime configuration pathที่ทำให้ Admin Microsoft OIDC loginดำเนินต่อโดยไม่ขอ Graph employee profile
- 1.31 IF target runtimeพบ legacy employee-profile switchเป็น missingหรือ false THEN THE SYSTEM SHALL ไม่ silentlyดำเนิน Microsoft loginต่อโดยไม่เรียก Graph

## REQ-2: EmployeeId normalization

**User Story:** As a security owner, I want normalize `employeeId` ด้วย policy เดียวของระบบก่อนใช้ทุกครั้ง, so that lookup, comparison และ persistence มี canonical valueเดียวกัน

**Acceptance Criteria (EARS):**

- 2.1 WHEN Graph คืน `employeeId` THE SYSTEM SHALL trim whitespace ที่ขอบก่อนใช้
- 2.2 IF `employeeId` หลัง trim เป็นค่าว่าง THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-missing`
- 2.3 IF `employeeId` มี control character THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 2.4 IF `employeeId` มี whitespace ภายใน THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 2.5 IF normalized `employeeId` ยาวเกิน maximum length ของ `admin.Users.EmployeeId` THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 2.6 WHEN `employeeId` ผ่าน validation THE SYSTEM SHALL normalize case ตาม `EmployeeIdPolicy` เดิม
- 2.7 THE SYSTEM SHALL reuse `EmployeeIdPolicy` และ schema limit เดิม
- 2.8 IF `employeeId` ไม่ผ่าน validation THEN THE SYSTEM SHALL ไม่ query `[dbo].[VibEmp]`
- 2.9 IF `employeeId` ไม่ผ่าน validation THEN THE SYSTEM SHALL ไม่สร้าง Admin session

## REQ-3: Read-only VibEmp lookup

**User Story:** As an employee, I wantระบบ resolve profile จาก HR source ที่กำหนดแบบ exact match, so that local profile ตรงกับแถวพนักงานโดยไม่เดาหรืออ่านข้อมูลเกินจำเป็น

**Acceptance Criteria (EARS):**

- 3.1 WHEN `employeeId` ผ่าน normalization THE SYSTEM SHALL query `[dbo].[VibEmp]` ด้วย predicate `WHERE EmpCode = @employeeId`
- 3.2 THE SYSTEM SHALL bind normalized `employeeId` เป็น SQL parameter
- 3.3 THE SYSTEM SHALL ไม่ใช้ string interpolation ใส่ค่า `employeeId` ลง SQL
- 3.4 THE SYSTEM SHALL ไม่ใช้ `LIKE`, prefix matching หรือ pattern matching ใน HR lookup
- 3.5 THE SYSTEM SHALL อ่านจาก `[dbo].[VibEmp]` เฉพาะ `EmpCode`, `FirstNameTh`, `LastNameTh`
- 3.6 THE SYSTEM SHALL ไม่อ่าน `[cfg].[VibEmp]`
- 3.7 THE SYSTEM SHALL ไม่อ่าน `DepartmentID`, branch, Office, Division, Position หรือ Level ใน employee profile decision path
- 3.8 THE SYSTEM SHALL ไม่อ่าน HR status, termination status หรือ employment eligibility ใน employee profile decision path
- 3.9 IF HR lookup คืน 0 แถว THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-missing`
- 3.10 WHEN HR lookup คืน 1 แถว THE SYSTEM SHALL map rowนั้นเป็น candidate profile
- 3.11 IF HR lookup คืนมากกว่า 1 แถว THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 3.12 THE SYSTEM SHALL ไม่ map `[dbo].[VibEmp]` เป็น writable EF entity
- 3.13 THE SYSTEM SHALL ไม่มี production INSERT, UPDATE หรือ DELETE path ต่อ `[dbo].[VibEmp]`
- 3.14 IF `[dbo].[VibEmp]` ไม่มี, permission denied, SQL command timeout หรือ SQL query ล้มเหลว THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`
- 3.15 IF HR source unavailable THEN THE SYSTEM SHALL ไม่ส่ง SQL error detail ไป browser
- 3.16 IF clientยกเลิก request THEN THE SYSTEM SHALL ยุติ callbackโดยไม่มี partial writeหรือ sessionโดยไม่รับประกัน denied-auth audit

## REQ-4: HR name validation และ mapping

**User Story:** As an employee, I wantชื่อจาก HR ถูก validate โดยไม่ตัดข้อมูลเงียบ, so that profile ที่บันทึกถูกต้องและไม่เสียอักขระ

**Acceptance Criteria (EARS):**

- 4.1 WHEN พบ HR row เดียว THE SYSTEM SHALL trim `FirstNameTh` ที่ขอบ
- 4.2 WHEN พบ HR row เดียว THE SYSTEM SHALL trim `LastNameTh` ที่ขอบ
- 4.3 IF `FirstNameTh` เป็น nullหรือว่างหลัง trim THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 4.4 IF `LastNameTh` เป็น nullหรือว่างหลัง trim THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 4.5 IF trimmed `FirstNameTh` ยาวเกิน maximum length ของ `admin.Users.FirstName` THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 4.6 IF trimmed `LastNameTh` ยาวเกิน maximum length ของ `admin.Users.LastName` THEN THE SYSTEM SHALL คืน typed outcome `employee-profile-invalid`
- 4.7 THE SYSTEM SHALL ไม่ truncate `FirstNameTh` หรือ `LastNameTh` เงียบ
- 4.8 WHEN name validation ผ่าน THE SYSTEM SHALL map `FirstName=trimmed FirstNameTh`
- 4.9 WHEN name validation ผ่าน THE SYSTEM SHALL map `LastName=trimmed LastNameTh`

## REQ-5: Existing Admin bind และ refresh

**User Story:** As an existing Admin, I want bind EmployeeId ครั้งแรกและ refreshชื่อโดยไม่เปลี่ยน identityหรือสิทธิ์, so that HR profileทันสมัยแต่ account ownershipคงเดิม

**Acceptance Criteria (EARS):**

- 5.1 WHEN exact tenant-aware Microsoft identity resolve Active Adminที่ `EmployeeId=NULL` THE SYSTEM SHALL bind normalized `employeeId`
- 5.2 WHEN exact tenant-aware Microsoft identity resolve Active Adminที่ `EmployeeId` ตรง normalized value THE SYSTEM SHALL คง `EmployeeId` เดิม
- 5.3 IF exact tenant-aware Microsoft identity resolve Adminที่ `EmployeeId` ต่างจาก normalized value THEN THE SYSTEM SHALL ปฏิเสธ callbackด้วย `identity-conflict`
- 5.4 IF bound `EmployeeId` ต่าง THEN THE SYSTEM SHALL ไม่ overwriteค่าที่ bindไว้
- 5.5 IF normalized `employeeId` ถูก Adminรายอื่นถือ THEN THE SYSTEM SHALL ปฏิเสธ callbackด้วย `identity-conflict`
- 5.6 WHEN HR profileผ่าน validation THE SYSTEM SHALL refresh `FirstName` และ `LastName` ทุก Microsoft login
- 5.7 WHEN `EmployeeId`, `FirstName`, `LastName` เท่าค่าเดิมทั้งหมด THE SYSTEM SHALL ไม่เพิ่ม resource `Version`
- 5.8 WHEN ค่าใดใน `EmployeeId`, `FirstName`, `LastName` เปลี่ยน THE SYSTEM SHALL persist profileสาม fieldเป็นค่าที่ resolveได้
- 5.9 WHEN profileเปลี่ยน THE SYSTEM SHALL เพิ่ม resource `Version` หนึ่งครั้ง
- 5.10 WHEN profileเปลี่ยน THE SYSTEM SHALL ไม่เพิ่ม `AuthorizationVersion`
- 5.11 WHEN profile syncสำเร็จ THE SYSTEM SHALL คง internal AdminIdเดิม
- 5.12 WHEN profile syncสำเร็จ THE SYSTEM SHALL คง Tierเดิม
- 5.13 WHEN profile syncสำเร็จ THE SYSTEM SHALL คง Role assignmentsเดิม
- 5.14 WHEN profile syncสำเร็จ THE SYSTEM SHALL คง `MerchantAccess` เดิม
- 5.15 WHEN profileเปลี่ยน THE SYSTEM SHALL stamp `UpdatedAt` ตาม aggregate persistence policy
- 5.16 WHEN profileเป็น no-op THE SYSTEM SHALL ไม่เปลี่ยน `UpdatedAt`
- 5.17 WHEN profile syncสำเร็จ THE SYSTEM SHALL ไม่เปลี่ยน `PositionId`
- 5.18 WHEN profile syncสำเร็จ THE SYSTEM SHALL ไม่เปลี่ยน `OfficeId`
- 5.19 WHEN profile syncสำเร็จ THE SYSTEM SHALL ไม่เปลี่ยน `LevelId`
- 5.20 WHEN profile syncสำเร็จ THE SYSTEM SHALL ไม่เปลี่ยน `DivisionId`
- 5.21 WHEN `EmployeeId` ถูก bindครั้งแรก THE SYSTEM SHALL append audit action `employee-bind` หนึ่งรายการใน identity/profile transaction
- 5.22 WHEN ชื่อของ existing Adminเปลี่ยน THE SYSTEM SHALL append audit action `employee-profile-sync` หนึ่งรายการใน identity/profile transaction
- 5.23 WHEN profileเป็น no-op THE SYSTEM SHALL ไม่ append `employee-bind` หรือ `employee-profile-sync` audit

## REQ-6: Atomic JIT และ transaction boundary

**User Story:** As a first-time employee, I want JIT identityและ HR profileถูกสร้างพร้อมกัน, so thatไม่มี partial accountหรือ sessionเมื่อ dependencyล้มเหลว

**Acceptance Criteria (EARS):**

- 6.1 WHEN exact tenant-aware Microsoft identityไม่พบและ profileผ่านครบ THE SYSTEM SHALL สร้าง JIT Adminตาม behaviorเดิม
- 6.2 WHEN JIT Adminถูกสร้าง THE SYSTEM SHALL กำหนด Statusเป็น `Active`
- 6.3 WHEN JIT Adminถูกสร้าง THE SYSTEM SHALL กำหนด Tierเป็น `Scoped`
- 6.4 WHEN JIT Adminถูกสร้าง THE SYSTEM SHALL ไม่ assign Role
- 6.5 WHEN JIT Adminถูกสร้าง THE SYSTEM SHALL ไม่สร้าง `MerchantAccess`
- 6.6 THE SYSTEM SHALL ทำ HR lookup, EmployeeId conflict check, JITหรือexisting-user mutation และ related `UserAudits` ภายใต้ Admin identity transactionและ lockเดียวกัน
- 6.7 THE SYSTEM SHALL persist `(Provider, TenantId, Subject)`, `EmployeeId`, `FirstName`, `LastName` และ related `UserAudits` ด้วย commitเดียวสำหรับ JIT Admin
- 6.8 IF profile resolutionล้มเหลว THEN THE SYSTEM SHALL rollback JIT Admin creation
- 6.9 IF profile resolutionล้มเหลว THEN THE SYSTEM SHALL rollback existing-user profile mutation
- 6.10 IF profile resolutionล้มเหลว THEN THE SYSTEM SHALL rollback related success, JIT, bind และ profile `UserAudits`
- 6.11 IF duplicate EmployeeId raceเกิดขึ้น THEN THE SYSTEM SHALL ให้ database unique indexเป็น final guard
- 6.12 IF callbackแพ้ duplicate EmployeeId race THEN THE SYSTEM SHALL คืน `identity-conflict`
- 6.13 IF callbackแพ้ duplicate EmployeeId race THEN THE SYSTEM SHALL ไม่มี partial write
- 6.14 WHEN identity/profile transaction commitสำเร็จ THE SYSTEM SHALL จึงสร้าง Admin session
- 6.15 IF identity/profile transactionไม่สำเร็จ THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 6.16 WHEN exact Microsoft identityตรง Suspended Admin THE SYSTEM SHALL ปฏิเสธก่อน query `[dbo].[VibEmp]`
- 6.17 IF tenant-aware identity conflictถูกตรวจพบก่อน profile resolution THEN THE SYSTEM SHALL ปฏิเสธก่อน query `[dbo].[VibEmp]`
- 6.18 IF bound `EmployeeId` ต่างจาก normalized value THEN THE SYSTEM SHALL ปฏิเสธก่อน query `[dbo].[VibEmp]`
- 6.19 IF normalized `employeeId` ถูก Adminรายอื่นถือ THEN THE SYSTEM SHALL ปฏิเสธก่อน query `[dbo].[VibEmp]`
- 6.20 WHEN mutation transaction rollbackเพราะ profile failure THE SYSTEM SHALL เขียน denied-auth auditภายหลังบน fresh scopeตาม current flow

## REQ-7: Denial, audit และ privacy

**User Story:** As a compliance owner, I wantทุก failure fail closedโดยไม่มี PIIหรือ infrastructure detailรั่ว, so that authentication denialตรวจสอบได้อย่างปลอดภัย

**Acceptance Criteria (EARS):**

- 7.1 IF Graphไม่มีหรือให้ invalid `employeeId` THEN THE SYSTEM SHALL เขียน denied-auth auditด้วย stable non-PII reason
- 7.2 IF HR profile missingหรือinvalid THEN THE SYSTEM SHALL เขียน denied-auth auditด้วย stable non-PII reason
- 7.3 IF HR source unavailable THEN THE SYSTEM SHALL เขียน denied-auth auditด้วย stable non-PII reason
- 7.4 IF EmployeeId mismatchหรือถูก Adminอื่นถือ THEN THE SYSTEM SHALL เขียน denied-auth auditด้วย stable non-PII reason
- 7.5 THE SYSTEM SHALL ไม่ใส่ access tokenใน logหรือ audit
- 7.6 THE SYSTEM SHALL ไม่ใส่ `employeeId` หรือ `EmpCode` ใน logหรือ audit
- 7.7 THE SYSTEM SHALL ไม่ใส่ `FirstNameTh`, `LastNameTh`, `FirstName` หรือ `LastName` ใน logหรือ audit
- 7.8 THE SYSTEM SHALL ไม่ใส่ Graph response bodyใน logหรือ audit
- 7.9 THE SYSTEM SHALL ไม่ใส่ SQL parameter valueใน logหรือ audit
- 7.10 THE SYSTEM SHALL ไม่ใส่ HR PIIหรือ SQL detailใน browser reasonหรือ response
- 7.11 THE SYSTEM SHALL ไม่ใช้ `WorkforceEmailKey`, email, UPNหรือ `preferred_username` ใน auth resolution, account bindingหรือ HR lookup decision path
- 7.12 THE SYSTEM SHALL ใช้ synthetic employee fixtureเท่านั้นใน test, specและ runbook
- 7.13 THE SYSTEM SHALL ใช้ browser reasonเพียง `employee-profile-missing`, `employee-profile-invalid`, `employee-profile-unavailable` หรือ `identity-conflict` สำหรับ denialใหม่ของฟีเจอร์นี้
- 7.14 THE SYSTEM SHALL ไม่มี production profile resolver pathที่คืน `employee-profile-unmapped`
- 7.15 THE SYSTEM SHALL ไม่ส่ง exception objectหรือ exception messageจาก Graphหรือ SQLเข้า application log
- 7.16 THE SYSTEM SHALL ไม่ใส่ OAuth provider `error_description`, AADSTS messageหรือ consent detailใน browser, log, auditหรือ session
- 7.17 WHEN exact provider error code `consent_required` ถูก classify THE SYSTEM SHALL logหรือ auditเฉพาะ stable non-PII categoryและ correlation ID

## REQ-8: Schema, uniqueness และ migration safety

**User Story:** As an operator, I want target schemaและสิทธิ์ DBเป็น final guardโดยไม่ทำให้ระบบเป็นเจ้าของ HR table, so thatfresh databaseและupgradeปลอดภัย

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL คง `admin.Users.EmployeeId` เป็น `nvarchar(16) NULL`
- 8.2 THE SYSTEM SHALL คง `admin.Users.FirstName` เป็น `nvarchar(500) NULL`
- 8.3 THE SYSTEM SHALL คง `admin.Users.LastName` เป็น `nvarchar(500) NULL`
- 8.4 THE SYSTEM SHALL enforce global filtered unique indexบน non-null `admin.Users.EmployeeId`
- 8.5 THE SYSTEM SHALL ใช้ global EmployeeId uniquenessขณะ productionยังใช้ workforce tenantเดียว
- 8.6 IF HEAD schemaตรง 8.1ถึง8.5อยู่แล้ว THEN THE SYSTEM SHALL ไม่เพิ่ม redundant columnหรือ index migration
- 8.7 THE SYSTEM SHALL ไม่สร้าง, alter, seedหรือ drop `[dbo].[VibEmp]` ใน EF migrationหรือ production bootstrap
- 8.8 THE SYSTEM SHALL ให้ fresh database migrateถึง HEADได้เมื่อ `[dbo].[VibEmp]` ไม่มี
- 8.9 WHEN `[dbo].[VibEmp]` มีอยู่ขณะ migration THE SYSTEM SHALL ให้ application principalมีเฉพาะสิทธิ์ `SELECT` ต่อตารางนี้
- 8.10 THE SYSTEM SHALL ไม่ grant `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `CONTROL` หรือ ownershipบน `[dbo].[VibEmp]` แก่ application principal
- 8.11 THE SYSTEM SHALL คง `[dbo].[VibEmp]` อยู่นอก runtime EF entity model
- 8.12 THE SYSTEM SHALL มี migrationหรือintegration assertionยืนยัน type, length, nullabilityและ EmployeeId index scope
- 8.13 WHEN operatorสร้าง `[dbo].[VibEmp]` หลัง migration THE SYSTEM SHALL ระบุ idempotent `GRANT SELECT ON dbo.VibEmp TO pol_app` stepใน runbook
- 8.14 WHILE `[dbo].[VibEmp]` มีอยู่แต่ application principalยังไม่มี `SELECT` THE SYSTEM SHALL คืน typed outcome `employee-profile-unavailable`

## REQ-9: Regression และ verification

**User Story:** As a maintainer, I want testsพิสูจน์ happy path, denialและ atomicityบน code pathจริง, so that auth/profile regressionไม่ผ่าน gateแบบ false green

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL มี testผ่าน OIDC callbackจริงสำหรับ Graph acquisitionและ employeeId forwarding
- 9.2 THE SYSTEM SHALL มี unit testของ `EmployeeIdPolicy` สำหรับ trim, blank, control character, internal whitespace, caseและ maximum length
- 9.3 THE SYSTEM SHALL มี testเรียก production HR readerผ่าน real `ControlPlaneDbContext`
- 9.4 THE SYSTEM SHALL มี testยืนยัน exact parameterized `EmpCode` query
- 9.5 THE SYSTEM SHALL มี testครอบ `[dbo].[VibEmp]` cardinality 0, 1และ 2 rows
- 9.6 THE SYSTEM SHALL มี testครอบ null, blankและ overlength names
- 9.7 THE SYSTEM SHALL มี testครอบ existing-user bind, refresh, no-opและ mismatch
- 9.8 THE SYSTEM SHALL มี testครอบ JIT identity/profile/audit transaction
- 9.9 THE SYSTEM SHALL มี testครอบ EmployeeId duplicateและ race outcome
- 9.10 THE SYSTEM SHALL มี testพิสูจน์ rollbackไม่มี partial user, profile, auditหรือ session
- 9.11 THE SYSTEM SHALL มี testครอบ missing table, permission denied, timeoutและ SQL errorเป็น unavailable outcome
- 9.12 THE SYSTEM SHALL มี staticหรือbehavior testยืนยัน PII-safe logging
- 9.13 THE SYSTEM SHALL มี static testยืนยัน Microsoft auth/profile pathไม่อ่าน `WorkforceEmailKey`หรือ emailเพื่อ decision
- 9.14 THE SYSTEM SHALL มี architecture testยืนยัน `[dbo].[VibEmp]`ไม่มี writable EF entityหรือ production write path
- 9.15 THE SYSTEM SHALL ผ่าน build, non-integration tests, integration tests, migration-script gate, full-tree secret scanและ spec trace
- 9.16 THE SYSTEM SHALL มี testยืนยัน Admin Microsoft authorization requestมี scopes `openid email profile User.Read`ทุกครั้ง
- 9.17 THE SYSTEM SHALL มี testยืนยัน valid callbackเรียก Graphหนึ่งครั้ง
- 9.18 THE SYSTEM SHALL มี testยืนยัน requestจาก existing Admin sessionและ session rotationไม่เรียก Graph
- 9.19 THE SYSTEM SHALL มี testยืนยัน missing access token, Graph `401`, Graph `403`และ exact `consent_required`คืน `employee-profile-unavailable`โดยไม่มี resolver mutationหรือ session
- 9.20 THE SYSTEM SHALL มี regression testยืนยัน user-cancelled `access_denied`ยังคืน `access-denied`และไม่ถูก mapเป็น profile failure

## EmployeeId uniqueness audit ก่อน design

ข้อกำหนดนี้เลือก **global uniqueness** เป็น baselineสำหรับ design gate ด้วยเหตุผลที่ตรวจได้จาก filesystemและ upstream contract:

| Evidence | ผล |
|---|---|
| runtimeยังรับ workforce tenantเดียว | ไม่มี approved HR namespaceต่อ tenant |
| `tier0-microsoft-tenant-aware-identity` REQ-5.25 pin global EmployeeId conflict/index | requirementใหม่ไม่ควรเปลี่ยน HR semanticsโดยอ้อม |
| current schemaมี filtered unique indexบน `EmployeeId` เดี่ยว | reuse final guardเดิมได้โดยไม่ migrationซ้ำ |
| Objectiveกำหนดว่า EmployeeIdถูก userอื่นถือให้ fail closed | tenant-scoped indexจะยอมให้ userสองรายถือค่าเดียวกัน |

การเปลี่ยนเป็น `(TenantId, EmployeeId)` ต้องเป็น follow-upที่มี HR-domain decisionและ multi-tenant admission requirementแยก ไม่รวมในงานนี้

## Edge Cases & Open Questions

ไม่มี open questionที่ขวาง requirements review สมมติฐานที่ล็อกใน draftนี้คือ:

- Microsoft Graph delegated `User.Read` เป็น sourceของ `employeeId`; optional/custom ID-token claimยังไม่ใช้
- profile syncทำทุก successful Microsoft login
- EmployeeId bindครั้งแรกแล้ว immutable
- `[dbo].[VibEmp]` เป็น external/operator-managed read-only table
- global EmployeeId uniquenessคงไว้ตาม auditด้านบน
- existing Office/Division fieldsบน `admin.Users` คงค่าปัจจุบันและไม่ถูกอ่านหรือเปลี่ยนโดย flowนี้
- Graph responseมีขนาดเล็กและใช้ HTTP/runtime parsing boundsเดิม; explicit response-size ceilingอยู่นอก scope
- historical `EmployeeId` ถูก persistผ่าน `EmployeeIdPolicy` เป็น canonical uppercaseอยู่แล้ว
- `LegacyKey`, Office/Division columns และ historical branch grantเป็น schema residueที่งานนี้ไม่ลบ
- denied-auth auditใช้ fresh scopeหลัง mutation rollbackและไม่ atomicกับ transactionที่ถูก rollback

### Amendment decisions (mandatory Graph on new login, 2026-09-03)

| ID | Decision | Requirement impact |
|---|---|---|
| M1 | Admin Microsoft employee profileเป็น mandatoryต่อ OIDC authorization/callbackใหม่ | REQ-1.5-1.6, 1.30-1.31 |
| M2 | existing session requestและ rotationไม่ใช่ loginใหม่และไม่เรียก Graph | REQ-1.24, 9.18 |
| M3 | missing access tokenและ Graph 401/403เป็น unavailable | REQ-1.25-1.26, 9.19 |
| M4 | classifyเฉพาะ exact provider error code `consent_required`;ห้าม parse description/AADSTS | REQ-1.27, 1.29, 7.16-7.17 |
| M5 | exact user-cancel `access_denied`คง behaviorเดิม | REQ-1.28, 9.20 |
| M6 | schema, identity, HR lookupและ atomic profile contractเดิมไม่เปลี่ยน | REQ-2ถึง REQ-8เดิม |

Designและ Tasksที่ approvedก่อน amendmentนี้ staleและต้อง syncหลัง Requirements amendmentได้รับ approval Tasksเดิมที่เสร็จแล้วต้องคง Evidenceไว้และเพิ่ม taskใหม่สำหรับ mandatory Graph delta

### Findings log (spec-analyze 2026-09-03, anchor `3e546ac` — requirements fileยัง untracked)

| ID | Category | Finding | Decision | Resolution |
|---|---|---|---|---|
| Q1 | logical inconsistency | conditional migration grantไม่ครอบ tableที่สร้างภายหลัง | A | เพิ่ม runbook grantและ unavailableก่อน grantใน REQ-8.13-8.14 |
| Q2 | logical inconsistency | profile refresh auditไม่มี actionที่กำหนด | A | `employee-bind`เฉพาะ first bind, `employee-profile-sync`เฉพาะชื่อเปลี่ยน, no-opไม่มี auditใน REQ-5.21-5.23 |
| Q3 | ambiguity | Graph URL pinขัด test seam | A | Production pin Graph URLและ testเปลี่ยน handler/base URLได้ใน REQ-1.6, 1.20-1.21 |
| Q4 | ambiguity | EmployeeId conflictกับ HR queryไม่มี precedence | A | conflictตรวจและ denyก่อน VibEmpใน REQ-6.18-6.19 |
| Q5 | ambiguity | SQL timeoutกับ client cancellationปนกัน | A | timeoutเป็น unavailable; client cancellationไม่มี partial write/sessionแต่ auditเป็น best-effortใน REQ-3.14, 3.16 |
| Q6 | conflicting constraints | UpdatedAtและ org fields preservationไม่ถูก pin | A | เพิ่ม UpdatedAt/no-opและ preserve Position/Office/Level/Divisionใน REQ-5.15-5.20 |
| Q7 | gap | Graph failureห้ามเพียง sessionแต่ไม่ห้าม identity/audit writes | A | เพิ่ม no identity/profile mutationและ no success auditsใน REQ-1.22-1.23 |
| Q8 | gap | Suspendedหรือ identity conflictอาจอ่าน HRก่อน denial | A | denyก่อน VibEmpใน REQ-6.16-6.17 |
| Q9 | gap | browser reasonไม่ได้ pin literalครบ | A | pin reasonทั้งสี่ใน REQ-7.13และ outcome criteria |
| Q10 | gap | `employee-profile-unmapped` ค้างหลังตัด mapping | A | retire production producerใน REQ-7.14; frontend legacy copyอาจคงได้ |
| A1 | unstated assumption | Graph response sizeไม่ได้กำหนด explicit ceiling | accepted | ใช้ HTTP/runtime boundsเดิมและบันทึกเป็น assumption |
| A2 | unstated assumption | historical EmployeeId caseอาจไม่ canonical | accepted | ถือว่า existing writesผ่าน policy; migration cleanupอยู่นอก scope |
| A3 | unstated assumption | old mapping schemaและ branch grantยังอยู่ | accepted | preserveเป็น residueโดยไม่แตะในงานนี้ |
| A4 | unstated assumption | denied auditไม่อยู่ใน failed mutation transaction | accepted | fresh-scope auditหลัง rollbackตาม current flow |
