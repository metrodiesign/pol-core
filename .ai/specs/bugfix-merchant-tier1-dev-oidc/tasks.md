# Tasks: Merchant Tier 1 Local DEV OIDC Configuration

หนึ่ง task ครบ public configuration, regression guards และ live Microsoft Entra External ID verification โดยไม่บันทึก credential ลง Git

> Status: approved 2026-08-17

- [x] 1. Pin Merchant Tier 1 local login to VCP External DEV and verify end-to-end behavior
  Satisfies: F1, F2, F3, F4, F5, F6, F7, B1, B2, B3, B4, B5, B6, B7, B8
  Verify: RED-before-fix local configuration test, targeted OIDC challenge/callback tests, solution build, secret scan, spec trace และ live login ด้วย rotated runtime credential
  Evidence:
  - Live Microsoft Entra External ID authorization, OTP, code redemption and verified-identity callback completed successfully
  - Callback redirected to `https://localhost:3002/register?ticket=<redacted>`; no credential, authorization code or ticket value was committed
  - Targeted Hosts regression passed 2/2; full non-integration suite passed 1756/1756
  - SQL migration integration suite passed 3/3; solution build passed with 0 warnings and 0 errors
  - `.ai/bin/check-secrets.sh --all`, spec trace and whitespace checks passed
  - Viewports: n/a — backend OIDC configuration and callback behavior only; no frontend source changed
  - Deviations: the managed sandbox could not open VSTest sockets, so final test evidence came from the user's local terminal
