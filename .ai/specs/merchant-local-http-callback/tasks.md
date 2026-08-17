# Tasks: Merchant Local HTTP Callback

หนึ่ง task ครบ config, regression guard และ live browser verification.

> Status: superseded 2026-08-17 — task นี้ไม่ได้ implement. แนวทางสุดท้ายใช้ local API แบบ HTTPS-only
> และ canonical Merchant callback; ดู `.ai/specs/bugfix-merchant-tier1-dev-oidc/`.

- Historical task (not implemented): Add the local HTTP callback listener, pin the Merchant Microsoft callback path and verify the generated authorization request
  Satisfies: REQ-1, REQ-2, REQ-3, REQ-4, REQ-5
  Verify: RED-before-fix architecture assertion, targeted OIDC challenge test, solution build and live Tier 1 login from port 5120
