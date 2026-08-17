# Implementation Tasks: Merchant SPA Runtime Origin

หนึ่ง task ปิด environment-precedence regression พร้อม observable redirect verification

> Status: approved 2026-08-17

- [x] 1. Pin canonical Merchant SPA origin at the local launch boundary, add a regression guard, restart the API and verify the live OIDC failure redirect
  Satisfies: F-1, F-2, F-3, B-1, B-2, B-3, B-4, B-5, B-6, B-7
  Verify: RED-before-fix launch-profile assertion, solution build, targeted origin/login tests, live callback failure probe and fresh Tier 1 authorization challenge
  Evidence:
  - Targeted Hosts regression passed 2/2 after the test host explicitly disabled the unrelated local Microsoft provider override
  - Full non-integration suite passed 1756/1756 with 0 failed and 0 skipped
  - Live Tier 1 callback used Merchant SPA origin `https://localhost:3002` and reached `/register` with the signed ticket redacted
  - Solution build passed with 0 warnings and 0 errors; secret scan, spec trace and whitespace checks passed
  - Viewports: n/a — backend configuration and redirect behavior only; no frontend source changed
  - Deviations: none
