# Implementation Tasks: Merchant SPA Runtime Origin

หนึ่ง task ปิด environment-precedence regression พร้อม observable redirect verification

> Status: approved 2026-08-17

- [x] 1. Pin canonical Merchant SPA origin at the local launch boundary, add a regression guard, restart the API and verify the live OIDC failure redirect
     Satisfies: F-1, F-2, F-3, B-1, B-2, B-3, B-4, B-5, B-6, B-7
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
