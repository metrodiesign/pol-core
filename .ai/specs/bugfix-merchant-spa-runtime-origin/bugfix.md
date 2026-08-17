# Bugfix: Merchant SPA Runtime Origin

ป้องกัน local API ใช้ environment override เก่าแล้วส่ง Merchant OIDC failure กลับไป port `5300`
แทน canonical Merchant SPA origin `https://localhost:3002`

> Status: approved 2026-08-17

## Current Behavior (Defect)

WHEN API process ที่ `https://localhost:5001` รับ Merchant Microsoft callback failure THEN API ตอบ `302`
ไป `http://localhost:5300/login-error?reason=auth-failed` แม้ committed และ ignored Development JSON
กำหนด `MerchantUser:Session:SpaBaseUrl=https://localhost:3002`

Reproduce:

```bash
curl -k -sS -D - -o /dev/null \
  'https://localhost:5001/api/v1/merchants/auth/microsoft/callback?error=access_denied&error_description=probe'
```

Observed:

```text
HTTP/2 302
location: http://localhost:5300/login-error?reason=auth-failed
```

Root cause: parent process environment มี legacy value
`MerchantUser__Session__SpaBaseUrl=http://localhost:5300`; environment provider มี precedence สูงกว่า JSON
และค่าถูก bind เข้า `UserSessionOptions` ตอน API start

## Expected Behavior

- F-1 WHEN API starts through local `https` launch profile THE SYSTEM SHALL use
  `https://localhost:3002` as effective `MerchantUser:Session:SpaBaseUrl`
- F-2 WHEN Merchant Microsoft OIDC remote failure occurs in Development THE SYSTEM SHALL return `302` with
  `Location=https://localhost:3002/login-error?reason=auth-failed`
- F-3 WHEN local runtime inherits legacy `MerchantUser__Session__SpaBaseUrl=http://localhost:5300` THE SYSTEM SHALL
  prevent that legacy value from overriding the canonical local launch-profile origin

## Unchanged Behavior

- B-1 WHEN Merchant Microsoft login starts directly from local API THE SYSTEM SHALL CONTINUE TO send callback URI
  `https://localhost:5001/api/v1/merchants/auth/microsoft/callback`
- B-2 WHEN OIDC state, nonce or code exchange fails THE SYSTEM SHALL CONTINUE TO deny session creation and redirect
  with a non-sensitive failure reason
- B-3 WHEN an Active merchant user completes login THE SYSTEM SHALL CONTINUE TO issue the merchant session cookie and
  redirect only to an allowlisted return path
- B-4 WHEN a NotFound or Rejected merchant identity completes login THE SYSTEM SHALL CONTINUE TO issue the applicable
  registration or correction ticket without creating a session
- B-5 WHEN `returnTo` is supplied THE SYSTEM SHALL CONTINUE TO reject absolute, protocol-relative and non-allowlisted
  redirect targets
- B-6 WHEN callback handling fails THE SYSTEM SHALL CONTINUE TO write denied-auth audit without logging token,
  credential or PII values
- B-7 WHEN API runs outside the local launch profile THE SYSTEM SHALL CONTINUE TO use deployment-provided SPA origin
  configuration without a hardcoded production URL

## Scope Constraints

- Do not modify secret-bearing `.env`
- Do not change Entra tenant, client ID, client secret or callback path
- Do not change production deployment origins
- Do not weaken OIDC state, nonce, PKCE, tenant, session, audit or open-redirect controls
