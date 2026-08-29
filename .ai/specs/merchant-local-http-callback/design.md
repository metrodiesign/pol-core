# Design: Merchant Local HTTP Callback

ใช้ ASP.NET Core launch profile และ OIDC configuration seam ที่มีอยู่แล้ว ไม่มี production branch ใหม่.

> Status: unknown
> ที่ `https://localhost:5001` และ canonical Merchant callback
> `/api/v1/merchants/auth/microsoft/callback`. ดู `.ai/specs/bugfix-merchant-tier1-dev-oidc/`.

## Configuration

- เพิ่ม `http://localhost:5120` ใน `applicationUrl` ของ local `https` profile.
- ตั้ง `MerchantAuth__Providers__Microsoft__CallbackPath=/auth/callback` ใน profile เดียวกัน.
- คง `MerchantUser__Session__SpaBaseUrl=https://localhost:3002`.

ASP.NET Core OIDC handler สร้าง redirect URI จาก request origin รวมกับ configured callback path. ดังนั้น test ต้องเริ่ม
จาก port `5120`; login ที่ port `5001` จะสร้าง origin ของ port `5001` ตามปกติ.

## Files

| File | Change |
|---|---|
| `src/Hosts/Api/Properties/launchSettings.json` | เพิ่ม listener และ callback override เฉพาะ local |
| `tests/Architecture.Tests/LocalDevelopmentOriginTests.cs` | lock committed launch configuration |
| `tests/Hosts.Tests/OidcCallbackE2ETests.cs` | assert authorization request redirect URI |

## Requirement Traceability

| Requirement | Design element |
|---|---|
| REQ-1 | local `applicationUrl` + architecture test |
| REQ-2 | OIDC callback override + authorization challenge test |
| REQ-3 | launch-profile environment variable + architecture test |
| REQ-4 | no change outside `launchSettings.json` |
| REQ-5 | existing Merchant SPA origin assertion |
