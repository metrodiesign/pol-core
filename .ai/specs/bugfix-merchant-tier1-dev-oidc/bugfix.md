# Bugfix: Merchant Tier 1 Local DEV OIDC Configuration

> Status: approved 2026-08-17

กำหนด Merchant Tier 1 local login ให้ใช้ `VCP External DEV` ผ่าน HTTPS callback ที่ลงทะเบียนไว้ โดยเก็บ credential นอก Git และนอกเอกสารทั้งหมด

## Current Behavior (Defect)

- WHEN local API เริ่ม Microsoft login ผ่าน `https://localhost:5001/api/v1/merchants/auth/microsoft/login` THEN launch profile ยัง override callback เป็น `/auth/callback` และเปิด temporary HTTP origin ที่ port `5120`
- WHEN Microsoft ส่ง authorization code กลับมาโดย runtime ไม่มี credential ที่ยังใช้ได้และไม่เคยเปิดเผย THEN code exchange ล้มเหลวและระบบ redirect ไป `https://localhost:3002/login-error?reason=auth-failed`

## Expected Behavior

- F-1 WHEN local Merchant Microsoft login เริ่มทำงาน THE SYSTEM SHALL ใช้ Authority `https://vcpexternaldev.ciamlogin.com/2a6d4554-88f1-4089-a995-0bf31c622493/v2.0`
- F-2 WHEN local Merchant Microsoft login เริ่มทำงาน THE SYSTEM SHALL ใช้ Client ID `dd7d2f17-60dc-4bd9-99a4-e2a93077bc9a`
- F-3 WHEN local Merchant Microsoft login สร้าง authorize request THE SYSTEM SHALL ส่ง `redirect_uri` เป็น `https://localhost:5001/api/v1/merchants/auth/microsoft/callback` แบบ exact match
- F-4 THE SYSTEM SHALL อ่าน client credential จาก `MerchantAuth__Providers__Microsoft__ClientSecret` ผ่าน runtime secret source เท่านั้น และ SHALL NOT เก็บค่าไว้ใน tracked file, test output, log หรือเอกสาร
- F-5 WHEN Infra ลงทะเบียน Web redirect ตาม F3 และ runtime มี credential ใหม่ที่ยังใช้ได้ THE SYSTEM SHALL แลก authorization code สำเร็จและเข้าสู่ MerchantUser outcome ปกติแทน `auth-failed`
- F-6 WHEN local launch profile ทำงาน THE SYSTEM SHALL ไม่ใช้ temporary callback `/auth/callback` หรือ HTTP callback port `5120` สำหรับ Merchant Tier 1
- F-7 WHEN automated OIDC callback test ทำงาน THE SYSTEM SHALL ตรวจ Authority, Client ID, HTTPS callback และ secret redaction ของ local configuration

## Unchanged Behavior

- B-1 WHEN Admin Tier 0 Microsoft login ทำงาน THE SYSTEM SHALL CONTINUE TO ใช้ tenant, client และ callback ของ Admin โดยไม่เปลี่ยน
- B-2 WHEN Google login ทำงาน THE SYSTEM SHALL CONTINUE TO ใช้ provider configuration เดิม
- B-3 WHEN OIDC flow ทำงาน THE SYSTEM SHALL CONTINUE TO บังคับ Authorization Code, PKCE, state, nonce, issuer, audience และ token lifetime validation
- B-4 WHEN Merchant SPA รับผล login THE SYSTEM SHALL CONTINUE TO ใช้ base URL `https://localhost:3002`
- B-5 WHEN verified identity ถูก resolve THE SYSTEM SHALL CONTINUE TO แยก Active, pending registration, Rejected และ inactive outcomes ตามเดิม
- B-6 WHEN authentication ล้มเหลว THE SYSTEM SHALL CONTINUE TO ส่งเหตุผลแบบ generic และ SHALL NOT เปิดเผย credential, token หรือ provider payload
- B-7 WHEN Entra application รองรับหลายองค์กร THE SYSTEM SHALL CONTINUE TO ยอมรับเฉพาะ issuer ของ tenant `VCP External DEV` ที่ pin ไว้
- B-8 WHEN redirect URI ถูกลงทะเบียนใน Entra THE SYSTEM SHALL CONTINUE TO ใช้ platform type `Web` ไม่ใช่ `SPA` หรือ public client
