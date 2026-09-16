# Bugfix: Agent CIAM account switch และ end-session

> Status: approved 2026-09-16

เอกสารนี้กำหนดการแก้ canonical Agent OAuth flow โดยคง Employee/Admin และ legacy MerchantUser auth ไว้เหมือนเดิม

## Current Behavior (Defect)

เมื่อ `pol-merchant` เรียก `/oauth/authorize` ด้วย `client_id=pol-merchant&prompt=select_account` ระบบสร้าง OIDC challenge ใหม่โดยไม่ส่ง `prompt` ต่อ ทำให้ downstream CIAM authorization URL ไม่มี `prompt` และ Microsoft อาจใช้ SSO account เดิม

เมื่อ Agent revoke platform authorization ผ่าน `POST /api/v1/auth/logout` ระบบไม่มี browser-navigation contract สำหรับจบ CIAM session โดยใช้ metadata และ post-logout URI ที่ server เป็นเจ้าของ

## Root Cause

- `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs:348-378` แยก Agent realm ได้ แต่สร้าง `AuthenticationProperties` โดยไม่ validate หรือ forward `OpenIddictRequest.Prompt`
- `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs:150-205` ตั้ง OIDC handler แต่ไม่มี end-session route ที่อ่าน `EndSessionEndpoint` จาก provider configuration
- `src/Api/Api/IdentityAccess/IdentityAccessOptions.cs:35-42` มี `AgentWebAppBaseUrl` แต่ยังไม่บังคับให้เป็น server-owned origin เมื่อเปิด Agent provider

## Expected Behavior

- F-1 เมื่อ Agent client ส่ง `prompt=select_account` ระบบต้องส่งค่าเดียวกันต่อไปยัง Microsoft CIAM authorization request
- F-2 เมื่อ Agent client ไม่ส่ง `prompt` ระบบต้องไม่เพิ่ม `prompt` เอง
- F-3 หาก Agent client ส่ง `prompt` ค่าอื่น ระบบต้องตอบ OAuth `400 invalid_request` โดยไม่เริ่ม OIDC challenge
- F-4 เมื่อ browser เปิด Agent CIAM logout endpoint ระบบต้อง redirect ไป `end_session_endpoint` จาก OIDC configuration และกำหนด `post_logout_redirect_uri` เป็น `<IdentityAccess:AgentWebAppBaseUrl>/login`
- F-5 หาก OIDC configuration ไม่มี `end_session_endpoint` ระบบต้อง fail closed ด้วย error ที่ตรวจสอบได้และไม่ redirect
- F-6 เมื่อ Agent Bearer เรียก `POST /api/v1/auth/logout` ระบบต้อง revoke authorization ตอบ `204` และ token เดิมต้องเรียก `/api/v1/me` ไม่ได้
- F-7 ระบบต้องไม่รับ browser-supplied redirect URL มา override post-logout destination
- F-8 เมื่อเปิด Agent OIDC provider ระบบต้อง fail startup หาก `IdentityAccess:AgentWebAppBaseUrl` ว่างหรือไม่ใช่ HTTP(S) origin ที่ไม่มี path, query หรือ fragment

## Unchanged Behavior

- B-1 เมื่อ Agent Login ไม่ส่ง `prompt` ระบบต้องคงใช้ Microsoft SSO session เดิมโดยไม่บังคับ `prompt=login`
- B-2 เมื่อ Employee/Admin client เรียก `/oauth/authorize` ระบบต้องคงพฤติกรรม prompt เดิมและไม่ forward Agent-only `select_account`
- B-3 เมื่อ legacy MerchantUser BFF ใช้ callback path ร่วม ระบบต้องคงส่ง callback ให้ scheme เจ้าของ state เดิม
- B-4 เมื่อ caller ที่ไม่มี Bearer เรียก `POST /api/v1/auth/logout` ระบบต้องคงตอบ `401`
- B-5 ระบบต้องไม่ใส่ authorization code, token, PKCE verifier, nonce, state หรือ registration session ใน end-session URL หรือ application log

## Scope Boundary

- แก้เฉพาะ canonical Agent OAuth flow ใต้ `src/Api/Api/IdentityAccess`
- ไม่แก้ source ของ `pol-merchant`, Admin/Employee login semantics หรือ legacy MerchantUser BFF auth
- ไม่แก้ Entra app registration หรือ production configuration
