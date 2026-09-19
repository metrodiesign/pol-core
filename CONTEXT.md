# pol-core

Payment/onboarding platform core (API + domain). context นี้เป็น control-plane ของ identity,
merchant และการลงทะเบียนตัวแทน (agent). glossary ด้านล่างนิยาม term ที่ปนกันง่ายในโดเมนนี้ โดยเฉพาะ
การแยก Account/LoginAccount และ admin/agent realm. สำหรับ product scope และ Non-Goals ดู
`.ai/shared/PROJECT_CONTEXT.md`.

## Language

### Identity และ Auth

**Account**:
Domain account ของผู้ใช้ มี `AccountType` = `Employee | Agent | System`.
_Avoid_: User, profile

**LoginAccount**:
การผูก `ExternalIdentity` กับ `Account` — มีได้ก็ต่อเมื่อ identity ถูก bind แล้วเท่านั้น.
_Avoid_: Credential, user login, identity record

**ExternalIdentity**:
ตัวตนจาก IdP ภายนอก = (`Provider`, `TenantId`, `ExternalUserId`).
_Avoid_: Subject, OIDC subject, external user

**AuthRealm**:
identity plane บน SPA เดียว = `admin | agent`; `admin` = พนักงาน (`AccountType.Employee`), `agent` =
ตัวแทน (`AccountType.Agent`).
_Avoid_: Role, plane, scope, user type

### การลงทะเบียนตัวแทน (Agent Registration)

**AgentRegistration**:
Aggregate ของการลงทะเบียนตัวแทนหนึ่งราย identity ของเคสคือ (`MerchantId`, `EmailNormalized`);
`ExternalIdentity` อาจยังไม่มีจนกว่าจะถูก bind ภายหลัง.
_Avoid_: Registration record, applicant, agent application

**ApplicantCase**:
มุมมองสถานะการลงทะเบียนที่ส่งให้ SPA มี `nextAction` (`submit | wait | login`).
_Avoid_: Application, registration view, case view

**Approved**:
สถานะของ `AgentRegistration` ที่ผ่าน reviewer แล้ว — มี `Account` แต่ยังไม่แปลว่า login ได้จนกว่าจะมี
`LoginAccount`.
_Avoid_: Active, verified, completed

**RegistrationSession**:
session แบบ anonymous ที่ผูกผู้สมัครกับเคสของตัวเอง มีอายุจำกัด.
_Avoid_: Anon session, guest session

**ContactVerification**:
การยืนยันเบอร์โทรด้วย OTP; "verified" หมายถึงยืนยันสำหรับเบอร์ปัจจุบันของเคสเท่านั้น เปลี่ยนเบอร์ต้อง
ยืนยันใหม่.
_Avoid_: OTP record, phone verification, contact check
