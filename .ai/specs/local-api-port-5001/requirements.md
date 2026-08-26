# Requirements: Local Development Origins

กำหนด origin สำหรับ backend API และ SPA ทั้งสามตัวใน local development เพื่อให้ runtime,
OIDC, PSP browser return, CORS, scripts และเอกสารอ้าง URL ตรงกัน

> Status: unknown

## REQ-1: Canonical local API origin

**User Story:** As a developer, I want local backend API ใช้ origin เดียวกับ Redirect URI ที่ลงทะเบียนใน Microsoft Entra, so that ทดสอบ OIDC และ local integrations ได้โดยไม่เกิด port หรือ scheme mismatch

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL expose backend API จาก committed local launch profile ที่ `https://localhost:5001` เท่านั้น
- 1.2 WHEN active local configuration, example, script หรือ current reference สร้าง backend API URL THE SYSTEM SHALL use `https://localhost:5001`
- 1.3 WHEN Development admin OIDC login resolves `returnTo=/scalar` THE SYSTEM SHALL redirect to `https://localhost:5001/scalar`
- 1.4 WHERE ignored `src/Hosts/Api/appsettings.Development.json` มีอยู่ในเครื่อง THE SYSTEM SHALL align `AdminSession:ScalarBaseUrl` และ `Psp:PublicBaseUrl` กับ `https://localhost:5001`
- 1.5 IF `5100` หรือ `5101` เป็น production-only setting, historical evidence หรือ SQL error code THEN THE SYSTEM SHALL preserve that value

## REQ-2: Canonical local SPA origins

**User Story:** As a developer, I want customer, admin and merchant SPAs to use distinct HTTPS origins, so that local redirects and browser access reach the correct frontend

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL use `https://localhost:3000` as the local customer SPA origin
- 2.2 THE SYSTEM SHALL use `https://localhost:3001` as the local admin SPA origin
- 2.3 THE SYSTEM SHALL use `https://localhost:3002` as the local merchant SPA origin
- 2.4 WHEN an admin OIDC callback completes in Development THE SYSTEM SHALL redirect frontend paths against `https://localhost:3001`
- 2.5 WHEN a merchant OIDC callback completes in Development THE SYSTEM SHALL redirect frontend paths against `https://localhost:3002`
- 2.6 WHEN a PSP returns the customer browser in Development THE SYSTEM SHALL target `https://localhost:3000/checkout/return`
- 2.7 WHERE local credentialed console CORS is configured THE SYSTEM SHALL allow merchant origin `https://localhost:3002` separately from admin origin `https://localhost:3001`
- 2.8 IF `5200` หรือ `5300` เป็น historical evidence THEN THE SYSTEM SHALL preserve that value

## Edge Cases and Assumptions

- Local HTTPS ต้องใช้ ASP.NET Core development certificate ที่เครื่องเชื่อถือ
- `.env` เป็น secret-bearing ignored file จึงไม่อ่านหรือแก้อัตโนมัติ; `.env.example` เป็น source สำหรับค่าที่ผู้ใช้ต้อง sync
- Docker production mapping และ self-host deployment runbook อยู่นอก local-development scope
- Customer SPA ใช้ same-origin `/api` proxy ตาม local topology จึงไม่ถูกเพิ่มเข้า credentialed console CORS
- Microsoft/Google callback URI ยังคงเป็น API origin `https://localhost:5001`; SPA origins เป็น post-login และ PSP browser-return targets
