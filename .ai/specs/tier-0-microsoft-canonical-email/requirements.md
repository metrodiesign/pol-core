# Requirements: Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)

> Status: approved 2026-08-23
> Notes:, amended 2026-08-23

## Overview

ฟีเจอร์นี้เปลี่ยน external identity ของพนักงานที่เข้า Admin Console ผ่าน Microsoft Entra ID
จาก Entra Object ID เป็น corporate email แบบ canonical และยกเลิก Entra App Role gate
`vcp.employee` โดยไม่เปลี่ยน authorization ภายในของ `pol-core`

คำว่า Tier 0 ในเอกสารนี้เป็นชื่อ authentication flow เท่านั้น ไม่ใช่ค่า `Tier.Scoped`
หรือ `Tier.Super` ใน domain model

## Scope และการ supersede

ข้อกำหนดนี้ supersede เฉพาะ Tier 0 behavior ที่ขัดกันใน `admin-workforce-jit`
และ `entra-scoped-preprovision` ส่วน OIDC, session และ authorization controls ที่ไม่ขัดกันยังคงเดิม

| พฤติกรรมเดิม | พฤติกรรมใหม่ | ผล |
|---|---|---|
| Microsoft subject ใช้ Entra `oid` | Microsoft subject ใช้ canonical corporate email | Supersede `admin-workforce-jit` REQ-2.5, 2.6, 2.24, 3.1, 3.2, 4.4 |
| Token ต้องมี exact App Role `vcp.employee` | Tier 0 ไม่อ่าน `roles` claim | Supersede `admin-workforce-jit` REQ-2.7, 2.8, 2.18, 2.22, 9.7 |
| Email ชน Admin เดิมแล้ว fail closed | Active Admin ที่มี canonical email ตรงกันถูก bind เข้าบัญชีเดิม | Supersede `admin-workforce-jit` REQ-4.1, 5.7-5.10 |
| Pre-provision รับ workforce tenant ID และ Entra Object ID | oid-based pre-provision ถูก retire | Supersede `entra-scoped-preprovision` REQ-1, REQ-3, REQ-4.1, 4.4, 4.6 |
| JIT ไม่ใช้ migration | oid-based Microsoft subject ถูก migrate แบบ fail closed | Supersede `admin-workforce-jit` REQ-10.6 |

Out of scope: Merchant authentication, provider อื่น, internal Tier/Roles/Permissions,
MerchantAccess, authorization policy, session protocol, CSRF policy และ UI redesign

## Canonical email definition

| ขั้น | กฎ |
|---|---|
| Claim selection | ใช้ `email` เมื่อมีหนึ่งค่า; fallback เป็น `preferred_username` เมื่อไม่มี `email` |
| Trim | ตัด whitespace หน้าและท้าย |
| Character set | ใช้ ASCII addr-spec |
| Format | standard-library parser ต้องคืน address ตรงกับค่าที่ trim แล้ว ไม่มี display name หรือ whitespace ภายใน |
| Length | ยาวไม่เกิน 254 ตัวอักษร |
| Normalize | ใช้ invariant lowercase กับทั้ง local part และ domain |
| Domain | ต้องเท่ากับ `viriyah.co.th` แบบ exact; ไม่รับ subdomain |

## REQ-1: Tier 0 OIDC security boundary

**User Story:** As a platform employee, I want ใช้ Microsoft workforce login ผ่าน flow เดิม, so that การเปลี่ยน identity key ไม่ลด OIDC security

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL เรียก authentication flow นี้ว่า `Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)` ใน current spec และ documentation
- 1.2 WHEN client เรียก `GET /api/v1/admins/auth/microsoft/login` THE SYSTEM SHALL เริ่ม Microsoft OIDC login ผ่าน middleware ปัจจุบัน
- 1.3 THE SYSTEM SHALL ใช้ Authorization Code flow สำหรับ Tier 0
- 1.4 THE SYSTEM SHALL ใช้ PKCE สำหรับ Tier 0
- 1.5 THE SYSTEM SHALL ตรวจ state สำหรับ Tier 0 callback
- 1.6 THE SYSTEM SHALL ตรวจ nonce สำหรับ Tier 0 callback
- 1.7 THE SYSTEM SHALL ตรวจ token signature สำหรับ Tier 0 callback
- 1.8 THE SYSTEM SHALL ตรวจ token issuer สำหรับ Tier 0 callback
- 1.9 THE SYSTEM SHALL ตรวจ token audience สำหรับ Tier 0 callback
- 1.10 THE SYSTEM SHALL ตรวจ token lifetime สำหรับ Tier 0 callback
- 1.11 THE SYSTEM SHALL คง callback URL `/api/v1/admins/auth/microsoft/callback`
- 1.12 IF signature audience lifetime state nonce หรือ code-exchange validation ล้มเหลว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `auth-failed`
- 1.13 THE SYSTEM SHALL ไม่เขียน OAuth token exchange ด้วย manual HTTP
- 1.14 IF issuer validation ล้มเหลว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`

## REQ-2: Workforce tenant และ canonical email validation

**User Story:** As a security owner, I want ตรวจ tenant และ corporate email แบบ deterministic, so that Tier 0 รับเฉพาะ identity ขององค์กร

**Acceptance Criteria (EARS):**

- 2.1 WHEN Microsoft token ผ่าน OIDC validation THE SYSTEM SHALL ตรวจ workforce claims ก่อน identity lookup
- 2.2 THE SYSTEM SHALL บังคับให้ token มี claim `tid` หนึ่งค่า
- 2.3 THE SYSTEM SHALL บังคับให้ claim `tid` เป็น non-empty UUID
- 2.4 THE SYSTEM SHALL บังคับให้ claim `tid` ตรง configured workforce tenant แบบ exact
- 2.5 WHEN token มี claim `email` หนึ่งค่า THE SYSTEM SHALL เลือก claim นั้นเป็น email identifier
- 2.6 WHEN token ไม่มี claim `email` และมี `preferred_username` หนึ่งค่า THE SYSTEM SHALL เลือก `preferred_username` เป็น email identifier
- 2.7 IF token มี claim `email` ที่ใช้ไม่ได้ THEN THE SYSTEM SHALL ไม่ fallback ไป `preferred_username`
- 2.8 IF token มี claim `email` มากกว่าหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.9 IF token ไม่มี `email` และมี `preferred_username` ไม่เท่ากับหนึ่งค่า THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.10 WHEN email identifier ถูกเลือก THE SYSTEM SHALL trim whitespace หน้าและท้าย
- 2.11 WHEN email identifier ผ่าน format validation THE SYSTEM SHALL normalize ด้วย invariant lowercase
- 2.12 THE SYSTEM SHALL บังคับให้ selected identifier เป็น mailbox เดียวแบบ ASCII addr-spec
- 2.13 THE SYSTEM SHALL บังคับให้ selected identifier ไม่มี display name
- 2.14 THE SYSTEM SHALL บังคับให้ selected identifier ไม่มี whitespace ภายใน
- 2.15 THE SYSTEM SHALL เปรียบเทียบ normalized domain กับ `viriyah.co.th` แบบ exact
- 2.16 IF normalized domain เป็น subdomain ของ `viriyah.co.th` THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.17 IF token ไม่มี usable corporate email THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.18 THE SYSTEM SHALL ไม่บังคับ อ่าน หรือ branch ตาม claim `roles` ใน Tier 0 production path
- 2.19 WHEN token ไม่มี claim `roles` THE SYSTEM SHALL ไม่ปฏิเสธ token ด้วยเหตุนี้
- 2.20 WHEN token มี claim `roles` เป็น empty array THE SYSTEM SHALL ไม่ปฏิเสธ token ด้วยเหตุนี้
- 2.21 WHEN token มี claim `roles` ที่ไม่มี `vcp.employee` THE SYSTEM SHALL ไม่ปฏิเสธ token ด้วยเหตุนี้
- 2.22 THE SYSTEM SHALL ไม่มี production-code reference ของ Tier 0 ไปยัง literal `vcp.employee`
- 2.23 THE SYSTEM SHALL ไม่บังคับ claim `oid` สำหรับ Tier 0 eligibility
- 2.24 THE SYSTEM SHALL ไม่ใช้ claim `oid` สำหรับ Tier 0 identity lookup
- 2.25 THE SYSTEM SHALL ไม่ใช้ claim `oid` สำหรับ Tier 0 identity persistence
- 2.26 IF workforce tenant หรือ canonical email validation ล้มเหลว THEN THE SYSTEM SHALL ปฏิเสธ callback ด้วย browser reason `workforce-access-denied`
- 2.27 IF workforce eligibility ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 2.28 THE SYSTEM SHALL ไม่เรียก Microsoft Graph ใน runtime Tier 0 login path
- 2.29 THE SYSTEM SHALL บังคับให้ selected identifier ยาวไม่เกิน 254 ตัวอักษร
- 2.30 THE SYSTEM SHALL ใช้ standard-library email parser ตรวจ selected identifier
- 2.31 IF parser คืน address ไม่ตรงกับ identifier ที่ trim แล้ว THEN THE SYSTEM SHALL ปฏิเสธ workforce eligibility
- 2.32 IF workforce eligibility ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin
- 2.33 IF workforce eligibility ล้มเหลว THEN THE SYSTEM SHALL ไม่ bind หรือเปลี่ยน identity
- 2.34 WHEN workforce eligibility ล้มเหลว THE SYSTEM SHALL คง denied-auth audit behavior ปัจจุบัน

## REQ-3: Canonical Microsoft identity resolution

**User Story:** As an employee, I want canonical corporate email resolve บัญชีเดิมแบบ case-insensitive, so that ตัวพิมพ์ต่างกันไม่สร้าง identity ซ้ำ

**Acceptance Criteria (EARS):**

- 3.1 THE SYSTEM SHALL ใช้คู่ `(provider=microsoft, subject=canonicalEmail)` เป็น Tier 0 identity key
- 3.2 THE SYSTEM SHALL ไม่ใช้ display name หรือชื่อบุคคลเป็น Tier 0 identity key
- 3.3 WHEN Microsoft identity ถูกสร้างหรือ bind THE SYSTEM SHALL persist subject เป็น canonical email
- 3.4 THE SYSTEM SHALL lookup Microsoft email subject แบบ case-insensitive
- 3.5 THE SYSTEM SHALL enforce Microsoft email subject uniqueness แบบ case-insensitive
- 3.6 WHEN canonical identity key ตรง Admin เดิม THE SYSTEM SHALL resolve Admin เดิม
- 3.7 WHEN canonical identity key ตรง Active Admin เดิม THE SYSTEM SHALL คง Tier เดิม
- 3.8 WHEN canonical identity key ตรง Active Admin เดิม THE SYSTEM SHALL คง Role assignments เดิม
- 3.9 WHEN canonical identity key ตรง Active Admin เดิม THE SYSTEM SHALL คง effective Permissions เดิม
- 3.10 WHEN canonical identity key ตรง Active Admin เดิม THE SYSTEM SHALL คง MerchantAccess เดิม
- 3.11 WHEN canonical identity key ตรง Active Admin เดิม THE SYSTEM SHALL resolve authorization state ปัจจุบัน
- 3.12 IF canonical identity key ตรง Suspended Admin THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session
- 3.13 IF canonical identity key ตรง Suspended Admin THEN THE SYSTEM SHALL ไม่สร้าง JIT Admin ใหม่

## REQ-4: Existing Admin binding และ least-privilege JIT

**User Story:** As an eligible employee, I want first login bind บัญชีเดิมหรือสร้างบัญชีขั้นต่ำ, so that ไม่มี Admin ซ้ำและไม่มีสิทธิ์อัตโนมัติ

**Acceptance Criteria (EARS):**

- 4.1 WHEN canonical Microsoft identity ยังไม่ถูก bind และมี Active Admin หนึ่งรายที่ canonical email ตรงกัน THE SYSTEM SHALL bind identity เข้ากับ Admin รายเดิม
- 4.2 WHEN Tier 0 bind Admin เดิมด้วย canonical email THE SYSTEM SHALL ไม่สร้าง Admin ใหม่
- 4.3 WHEN Tier 0 bind Admin เดิมด้วย canonical email THE SYSTEM SHALL คง Tier เดิม
- 4.4 WHEN Tier 0 bind Admin เดิมด้วย canonical email THE SYSTEM SHALL คง Role assignments เดิม
- 4.5 WHEN Tier 0 bind Admin เดิมด้วย canonical email THE SYSTEM SHALL คง Permissions เดิม
- 4.6 WHEN Tier 0 bind Admin เดิมด้วย canonical email THE SYSTEM SHALL คง MerchantAccess เดิม
- 4.7 IF email-matched Admin มี Subject ไม่เป็น NULL และ identity ที่ bind อยู่เป็น provider อื่นหรือ Microsoft subject อื่นที่ยังไม่ผ่าน migration THEN THE SYSTEM SHALL คืน identity conflict
- 4.8 IF email-matched Admin มี identity conflict THEN THE SYSTEM SHALL ไม่ overwrite identity เดิม
- 4.9 IF email-matched Admin เป็น Suspended THEN THE SYSTEM SHALL ไม่ bind identity ระหว่าง login
- 4.10 IF email-matched Admin เป็น Suspended THEN THE SYSTEM SHALL ปฏิเสธการสร้าง session
- 4.11 WHEN canonical email ไม่ตรง Admin หรือ identity ใด THE SYSTEM SHALL JIT-create Admin ใหม่
- 4.12 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL กำหนด Status เป็น `Active`
- 4.13 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL กำหนด Tier เป็น `Scoped`
- 4.14 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL ไม่ assign Role ใด
- 4.15 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL ไม่ assign MerchantAccess ใด
- 4.16 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL ไม่ grant Permission ใดโดยนัย
- 4.17 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL persist email เป็น canonical email
- 4.18 THE SYSTEM SHALL commit Admin creation หรือ existing binding กับ identity audit ใน transaction เดียวกัน
- 4.19 WHEN Tier 0 resolution สำเร็จสำหรับ Active Admin THE SYSTEM SHALL สร้าง Admin session ตาม flow ปัจจุบัน
- 4.20 IF Tier 0 identity mutation ล้มเหลว THEN THE SYSTEM SHALL ไม่เหลือ Admin หรือ binding บางส่วน
- 4.21 IF Tier 0 identity mutation ล้มเหลว THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 4.22 WHEN Tier 0 เปรียบเทียบ claim กับ stored Admin email THE SYSTEM SHALL canonicalize stored email ด้วยกฎเดียวกับ claim
- 4.23 WHEN Tier 0 bind Admin เดิม THE SYSTEM SHALL ไม่เปลี่ยน stored Admin email
- 4.24 THE SYSTEM SHALL ถือว่า external identity ของ Admin ถูก bind ต่อเมื่อ Subject ไม่เป็น NULL
- 4.25 WHEN Admin มี Subject เป็น NULL THE SYSTEM SHALL ถือว่า stored Provider เป็น unbound placeholder ที่ Microsoft binding เปลี่ยนได้

## REQ-5: Conflict และ concurrency safety

**User Story:** As a security owner, I want concurrent login และ ambiguous email fail closed, so that identity ownership ไม่ถูก merge ผิดคน

**Acceptance Criteria (EARS):**

- 5.1 THE SYSTEM SHALL serialize Tier 0 identity mutation ผ่าน identity mutation lock ปัจจุบัน
- 5.2 IF canonical email ตรง Admin มากกว่าหนึ่งราย THEN THE SYSTEM SHALL fail closed
- 5.3 IF canonical email ตรง Microsoft identity มากกว่าหนึ่งรายการ THEN THE SYSTEM SHALL fail closed
- 5.4 IF Admin match และ identity match ชี้คนละ Admin THEN THE SYSTEM SHALL fail closed
- 5.5 IF callback หลายรายการ bind canonical identity เดียวกันพร้อมกัน THEN THE SYSTEM SHALL bind ได้ไม่เกินหนึ่ง Admin
- 5.6 IF callback หลายรายการ JIT canonical identity เดียวกันพร้อมกัน THEN THE SYSTEM SHALL สร้าง Admin ได้ไม่เกินหนึ่งราย
- 5.7 IF callback หลายรายการ JIT canonical identity เดียวกันพร้อมกัน THEN THE SYSTEM SHALL เขียน JIT audit ได้ไม่เกินหนึ่งรายการ
- 5.8 WHEN concurrent winner commit สำเร็จ THE SYSTEM SHALL ให้ callback ที่ตามม resolve Admin รายเดียวกัน
- 5.9 IF unique constraint race ไม่สามารถ re-resolve เป็น Admin รายเดียวกัน THEN THE SYSTEM SHALL คืน identity conflict
- 5.10 IF identity conflict เกิดขึ้น THEN THE SYSTEM SHALL ไม่สร้าง Admin session
- 5.11 IF identity conflict เกิดขึ้น THEN THE SYSTEM SHALL ไม่เปิดเผย email หรือ record ที่ชนใน browser reason
- 5.12 IF callback หลายรายการ bind existing Admin พร้อมกัน THEN THE SYSTEM SHALL เขียน binding audit ได้ไม่เกินหนึ่งรายการ

## REQ-6: Existing oid identity migration และ rollback

**User Story:** As an operator, I want migrate oid-based identity แบบ fail closed, so that deployment ไม่ลบหรือสลับเจ้าของ Admin เดิม

**Acceptance Criteria (EARS):**

- 6.1 WHEN migration พบ Admin ที่มี `(provider=microsoft, subject=oid)` THE SYSTEM SHALL derive candidate subject จาก Admin email ของ row เดียวกัน
- 6.2 WHEN migration derive candidate subject THE SYSTEM SHALL ใช้ canonical email definition เดียวกับ runtime
- 6.3 WHEN migration candidate valid และ unique THE SYSTEM SHALL เปลี่ยน Microsoft subject ของ Admin row เดิมเป็น canonical email
- 6.4 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง internal Admin ID เดิม
- 6.5 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง Status เดิม
- 6.6 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง Tier เดิม
- 6.7 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง Role assignments เดิม
- 6.8 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง Permissions เดิม
- 6.9 WHEN migration เปลี่ยน Microsoft subject THE SYSTEM SHALL คง MerchantAccess เดิม
- 6.10 THE SYSTEM SHALL ไม่สร้าง Admin ใหม่ระหว่าง oid identity migration
- 6.11 THE SYSTEM SHALL ไม่ลบ Admin ระหว่าง oid identity migration
- 6.12 IF migration candidate email หาย malformed หรือผิด corporate domain THEN THE SYSTEM SHALL abort migration
- 6.13 IF migration candidates ซ้ำกันหลัง canonicalization THEN THE SYSTEM SHALL abort migration
- 6.14 IF migration candidate ชน Microsoft email subject ที่มีอยู่ THEN THE SYSTEM SHALL abort migration
- 6.15 IF migration พบ identity ownership ที่ ambiguous THEN THE SYSTEM SHALL abort migration
- 6.16 IF oid identity migration abort THEN THE SYSTEM SHALL ไม่เหลือ partial conversion
- 6.17 THE SYSTEM SHALL preserve rollback data ที่จำเป็นต่อการ restore oid-based subject เดิม
- 6.18 THE SYSTEM SHALL ไม่ใช้ runtime dual lookup ที่ยอมรับ oid หลัง migration
- 6.19 WHEN client เรียก `PUT /api/v1/admins/{id:guid}/microsoft-identity` THE SYSTEM SHALL ตอบ normal `404` โดยไม่เปลี่ยน identity
- 6.20 THE SYSTEM SHALL มี migration compatibility test สำหรับ valid unique oid-based identity
- 6.21 THE SYSTEM SHALL มี migration compatibility test สำหรับ invalid email
- 6.22 THE SYSTEM SHALL มี migration compatibility test สำหรับ duplicate canonical email
- 6.23 WHEN Microsoft subject เป็น canonical email ที่ตรง derived candidate อยู่แล้ว THE SYSTEM SHALL ถือ migration row เป็น no-op
- 6.24 IF Microsoft subject ไม่ใช่ UUID และไม่ใช่ canonical email ที่ตรง derived candidate THEN THE SYSTEM SHALL abort migration
- 6.25 WHERE oid identity migration ถูก deploy THE SYSTEM SHALL ป้องกัน mixed-version Tier 0 traffic ด้วย maintenance window
- 6.26 IF migration verification ล้มเหลวก่อนเปิด Tier 0 traffic THEN THE SYSTEM SHALL restore oid subjects และ prior application version
- 6.27 WHEN Tier 0 traffic เปิดหลัง migration แล้ว THE SYSTEM SHALL ใช้ forward recovery แทน rollback ไป oid-only application
- 6.28 THE SYSTEM SHALL มี migration compatibility test สำหรับ canonical-email no-op และ unknown subject rejection

## REQ-7: Session, authorization, audit และ privacy

**User Story:** As an auditor, I want Tier 0 เปลี่ยนเฉพาะ authentication identity, so that authorization และข้อมูลอ่อนไหวไม่รั่วหรือถูกยกระดับ

**Acceptance Criteria (EARS):**

- 7.1 THE SYSTEM SHALL ไม่ map Entra App Role ไป internal Role ของ `pol-core`
- 7.2 THE SYSTEM SHALL ไม่ map Entra App Role ไป internal Permission ของ `pol-core`
- 7.3 THE SYSTEM SHALL ไม่ grant `platform_admin` จาก Tier 0 login
- 7.4 THE SYSTEM SHALL ไม่ grant `Tier.Super` จาก Tier 0 login
- 7.5 THE SYSTEM SHALL resolve effective Permissions สดจาก internal Role assignments ปัจจุบัน
- 7.6 THE SYSTEM SHALL resolve accessible merchants สดจาก MerchantAccess ปัจจุบัน
- 7.7 THE SYSTEM SHALL คง session rotation contract ปัจจุบัน
- 7.8 THE SYSTEM SHALL คง session revocation contract ปัจจุบัน
- 7.9 THE SYSTEM SHALL คง CSRF contract ปัจจุบัน
- 7.10 WHEN Tier 0 bind existing Admin THE SYSTEM SHALL append identity-binding audit ด้วย internal Admin ID
- 7.11 WHEN Tier 0 JIT-create Admin THE SYSTEM SHALL append `jit-provision` audit ด้วย internal Admin ID
- 7.12 THE SYSTEM SHALL ไม่บันทึก raw `oid` ใน identity audit หรือ application log
- 7.13 THE SYSTEM SHALL ไม่บันทึก canonical email ใน Tier 0 identity audit
- 7.14 THE SYSTEM SHALL ไม่บันทึก canonical email ใน Tier 0 application log
- 7.15 THE SYSTEM SHALL ไม่บันทึก authorization code ใน audit หรือ application log
- 7.16 THE SYSTEM SHALL ไม่บันทึก ID token ใน audit หรือ application log
- 7.17 THE SYSTEM SHALL ไม่บันทึก access token ใน audit หรือ application log
- 7.18 THE SYSTEM SHALL ไม่บันทึก cookie หรือ session token ใน audit หรือ application log
- 7.19 WHEN Tier 0 bind existing Admin THE SYSTEM SHALL ใช้ audit action `microsoft-email-bind`
- 7.20 WHEN Tier 0 bind existing Admin THE SYSTEM SHALL ใช้ internal Admin ID เดียวกันเป็น actor และ target
- 7.21 WHEN Tier 0 bind existing Admin THE SYSTEM SHALL เขียน binding audit หนึ่งรายการต่อ unbound-to-bound transition

## REQ-8: Regression boundaries

**User Story:** As a maintainer, I want Tier 0 change อยู่ใน auth boundary, so that console และ provider อื่นไม่ regress

**Acceptance Criteria (EARS):**

- 8.1 THE SYSTEM SHALL ไม่เปลี่ยน Merchant Google authentication behavior
- 8.2 THE SYSTEM SHALL ไม่เปลี่ยน Merchant Microsoft authentication behavior
- 8.3 THE SYSTEM SHALL ไม่เปลี่ยน Admin Google retirement behavior
- 8.4 THE SYSTEM SHALL ไม่เปลี่ยน wire shape ของ `GET /api/v1/admins/me`
- 8.5 THE SYSTEM SHALL ไม่เปลี่ยน internal Role API contracts
- 8.6 THE SYSTEM SHALL ไม่เปลี่ยน internal Permission policy
- 8.7 THE SYSTEM SHALL ไม่เปลี่ยน MerchantAccess API contracts
- 8.8 THE SYSTEM SHALL ไม่เพิ่ม runtime dependency เมื่อ standard library หรือ dependency เดิมทำ canonical email validation ได้

## REQ-9: Documentation, rollout และ verification

**User Story:** As an operator, I want current documentation และ tests ตรงกับ Tier 0 behavior ใหม่, so that rollout ไม่พึ่ง App Role หรือรายงาน false green

**Acceptance Criteria (EARS):**

- 9.1 THE SYSTEM SHALL ระบุชื่อ `Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)` ใน current authentication documentation
- 9.2 THE SYSTEM SHALL ไม่ระบุ `vcp.employee` เป็น Tier 0 login prerequisite ใน current documentation
- 9.3 THE SYSTEM SHALL ระบุว่า Entra App Role ไม่ใช่ internal Role หรือ Permission ของ `pol-core`
- 9.4 THE SYSTEM SHALL document canonical email claim precedence และ validation rules
- 9.5 THE SYSTEM SHALL document migration preflight ที่ fail closed
- 9.6 THE SYSTEM SHALL document production backup ก่อน migration
- 9.7 THE SYSTEM SHALL document rollback procedure สำหรับ legacy oid subject
- 9.8 THE SYSTEM SHALL document residual risk ของ corporate email rename
- 9.9 THE SYSTEM SHALL document residual risk ของ corporate email reuse
- 9.10 THE SYSTEM SHALL document ว่า lifecycle owner ต้อง suspend Admin ของเจ้าของเดิมก่อนองค์กร reuse email
- 9.11 THE SYSTEM SHALL ตั้งชื่อ authentication tests ให้ระบุ Tier 0 behavior ชัดเจน
- 9.12 THE SYSTEM SHALL มี automated test ยืนยัน login เมื่อ `roles` claim ไม่มี
- 9.13 THE SYSTEM SHALL มี automated test ยืนยัน login เมื่อ `roles` claim เป็น empty array
- 9.14 THE SYSTEM SHALL มี automated test ยืนยัน login เมื่อ `roles` claim ไม่มี `vcp.employee`
- 9.15 THE SYSTEM SHALL มี automated test ยืนยัน wrong tenant ถูกปฏิเสธ
- 9.16 THE SYSTEM SHALL มี automated test ยืนยัน malformed email ถูกปฏิเสธ
- 9.17 THE SYSTEM SHALL มี automated test ยืนยัน wrong domain ถูกปฏิเสธ
- 9.18 THE SYSTEM SHALL มี automated test ยืนยัน missing usable email ถูกปฏิเสธ
- 9.19 THE SYSTEM SHALL มี automated test ยืนยัน suspended Admin ถูกปฏิเสธ
- 9.20 THE SYSTEM SHALL มี automated test ยืนยัน email case variants resolve Admin รายเดียวกัน
- 9.21 THE SYSTEM SHALL มี automated test ยืนยัน existing Admin binding ไม่สร้าง Admin ซ้ำ
- 9.22 THE SYSTEM SHALL มี automated test ยืนยัน JIT Admin เป็น Active Scoped roleless และไม่มี MerchantAccess
- 9.23 THE SYSTEM SHALL มี automated test ยืนยัน Tier Roles Permissions และ MerchantAccess ของ Admin เดิมไม่เปลี่ยนหลัง login
- 9.24 THE SYSTEM SHALL มี OIDC callback integration test สำหรับ Tier 0 canonical email identity
- 9.25 THE SYSTEM SHALL มี database migration integration test สำหรับ oid compatibility cases
- 9.26 THE SYSTEM SHALL รัน targeted tests และ repository build typecheck lint gates ที่มีอยู่จริง
- 9.27 WHEN verification infrastructure ล้มก่อน assertion THE SYSTEM SHALL รายงาน verification เป็น failed หรือ blocked
- 9.28 THE SYSTEM SHALL document maintenance window mixed-version prohibition และ rollback cutoff ของ oid migration

## Verification matrix

| กลุ่ม | Scenario ขั้นต่ำ | ครอบคลุม |
|---|---|---|
| OIDC protocol | code, PKCE, state, nonce, signature, issuer, audience, lifetime | REQ-1 |
| Workforce claims | exact tenant, canonical email, missing/empty/unrelated roles, ignored oid | REQ-2 |
| Identity resolution | canonical subject, case-insensitive lookup และ uniqueness | REQ-3 |
| Existing Admin | bind Active account เดิม, preserve authorization, reject Suspended/conflict | REQ-4 |
| JIT | unknown email ได้ Active Scoped roleless no MerchantAccess | REQ-4 |
| Concurrency | concurrent bind/JIT ได้ Admin และ audit เดียว | REQ-5 |
| Migration | valid unique converts; invalid duplicate หรือ ambiguous aborts atomically | REQ-6 |
| Privacy | audit/log ไม่มี token, code, cookie หรือ raw oid | REQ-7 |
| Regression | Merchant auth และ internal authorization contracts ไม่เปลี่ยน | REQ-8 |
| Documentation | current docs ไม่มี App Role prerequisite และมี rollback/risk runbook | REQ-9 |

## Edge cases & open questions

Product decisions ที่ approve แล้ว:

- `email` มี precedence เหนือ `preferred_username`; fallback ใช้เมื่อ `email` ไม่มีเท่านั้น
- Active Admin ที่ email ตรงและยัง unbound ถูก bind เข้าบัญชีเดิม
- Admin ที่มี bound Subject จาก provider อื่นหรือ Microsoft subject อื่นอยู่แล้วคืน identity conflict; ไม่ overwrite
- Admin ที่ Subject เป็น `NULL` ยัง unbound แม้ stored Provider เป็นค่า default `google`; Microsoft binding เปลี่ยน placeholder นี้ได้
- Suspended Admin ไม่ถูก bind ระหว่าง login และไม่ได้ session
- Legacy route `PUT /api/v1/admins/{id:guid}/microsoft-identity` ถูก retire เป็น normal `404`
- Migration รับ UUID legacy row และ canonical-email no-op เท่านั้น; subject รูปแบบอื่นทำให้ abort
- Migration derive canonical subject จาก `admin.Users.Email` โดยไม่แก้ stored email
- Migration ใช้ maintenance window ไม่มี mixed-version Tier 0 traffic
- Email validator รับ ASCII addr-spec ยาวสุด 254 ตัวอักษรผ่าน standard library
- Existing-Admin binding audit ใช้ action `microsoft-email-bind` และ Admin ID เป็น actor/target
- Email rename ไม่ auto-transfer authorization; email ใหม่ที่ไม่ match เริ่มเป็น roleless JIT account
- Email reuse อาจ resolve บัญชีเดิมได้; lifecycle process ต้อง suspend บัญชีเจ้าของเดิมก่อน reuse

Residual risk ที่ยังคงอยู่โดยธรรมชาติ: email เป็น mutable/reusable identifier จึงไม่พิสูจน์ continuity
ของบุคคลได้เท่า `oid` ระบบลดผลกระทบด้วย exact tenant/domain, fail-closed conflicts, roleless JIT,
audit และ suspended-account rejection แต่ต้องพึ่ง employee lifecycle control สำหรับ rename/reuse

### Findings log (spec-analyze) — anchor: `2601f71` (requirements.md uncommitted at analyze time)

| Finding | Category | REQ | Decision | Resolution |
|---|---|---|---|---|
| F1 | conflicting constraints | 6.17-6.18 | A | ใช้ maintenance window ไม่มี mixed version; rollback oid-only ได้ก่อนเปิด traffic หลังจากนั้นใช้ forward recovery |
| F2 | gap | 6.1-6.3 | A | canonical subject ที่ตรง candidate เป็น no-op; UUID ถูก migrate; subject อื่น abort |
| F3 | logical inconsistency | 6.19, 9.10 | A | retire endpoint เป็น normal 404 และกำหนด lifecycle mitigation เป็น suspend-only |
| F4 | gap | 2.26-2.27 | A | eligibility failure ห้าม account/identity mutation แต่ยังเขียน denied-auth audit ได้ |
| F5 | ambiguity | 1.12, 2.26 | A | invalid issuer ใช้ `workforce-access-denied`; protocol/crypto failure อื่นใช้ `auth-failed` |
| F6 | unstated assumption | 2.12-2.14, 3.3 | A | รับ ASCII addr-spec ยาวสุด 254 ผ่าน standard library พร้อม exact parsed-address check |
| F7 | ambiguity | 3.4, 4.1 | A | canonicalize stored email เพื่อ compare โดยไม่เปลี่ยน stored value; collision fail closed |
| F8 | gap, concurrency | 4.18, 5.5-5.8, 7.10 | A | ใช้ `microsoft-email-bind`, actor/target เป็น Admin เดียวกัน และเขียน audit ครั้งเดียวต่อ transition |
| F9 | design inconsistency | 4.1, 4.7 | A | นิยาม bound จาก `Subject IS NOT NULL`; Provider ของ row ที่ Subject เป็น NULL เป็น placeholder ไม่ใช่ identity conflict |
