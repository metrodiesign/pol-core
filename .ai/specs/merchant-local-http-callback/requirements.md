# Requirements: Merchant Local HTTP Callback

เพิ่ม callback สำหรับทดสอบ Tier 1 ผ่าน HTTP localhost โดยไม่ถอด local HTTPS API และไม่กระทบ production.

> Status: superseded 2026-08-17 — ไม่ได้ implement เพราะแนวทางสุดท้ายคง local API แบบ HTTPS-only ที่
> `https://localhost:5001` และใช้ canonical Merchant callback
> `/api/v1/merchants/auth/microsoft/callback`. ดู `.ai/specs/bugfix-merchant-tier1-dev-oidc/`.
> เอกสารนี้เก็บไว้เป็นประวัติของแนวทางทดสอบที่ถูกยกเลิก.

## REQ-1: Local listeners

- 1.1 WHEN API starts through the local `https` launch profile THE SYSTEM SHALL listen on both
  `https://localhost:5001` and `http://localhost:5120`.

## REQ-2: Authorization redirect URI

- 2.1 WHEN Merchant Microsoft login starts from `http://localhost:5120` THE SYSTEM SHALL send
  `redirect_uri=http://localhost:5120/auth/callback` to Entra External ID.

## REQ-3: Local callback override

- 3.1 WHEN API starts through the local `https` launch profile THE SYSTEM SHALL set only the Merchant Microsoft
  callback path override to `/auth/callback`.

## REQ-4: Deployment isolation

- 4.1 WHEN API runs outside the local launch profile THE SYSTEM SHALL continue using deployment-provided listener
  origins and Merchant Microsoft callback configuration.

## REQ-5: Merchant SPA result origin

- 5.1 WHEN local Merchant authentication succeeds or fails THE SYSTEM SHALL continue redirecting browser results to
  the Merchant SPA origin `https://localhost:3002`.

## Constraints

- เริ่ม test login จาก `http://localhost:5120/api/v1/merchants/auth/microsoft/login` เท่านั้น.
- ไม่เปลี่ยน Client ID, Client secret, Authority, tenant validation, PKCE, state, nonce หรือ session security.
- ไม่เปลี่ยน Admin Microsoft และ Google callback.

## Self-check

- ไม่มี requirement ขัดกัน: port `5001` ยังอยู่และ `5120` เป็น listener เพิ่มเฉพาะ local.
- Success criterion วัดได้จาก authorization request parameter และ regression tests.
- Unstated assumption ถูกบันทึกแล้ว: redirect origin มาจาก origin ที่ใช้เริ่ม login.
