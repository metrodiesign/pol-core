# Implementation Tasks: Local Development Origins

สอง task ครอบ API และ SPA origin contracts พร้อม verification

> Status: unknown

- [x] 1. Make `https://localhost:5001` the canonical local API origin across launch configuration, active local examples, scripts, current docs and Scalar redirect tests while preserving production and historical values
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
- [x] 2. Align customer, admin and merchant local SPA origins across committed/local config, OIDC redirects, PSP browser returns, current docs and regression tests while preserving historical evidence
     Satisfies: REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7, REQ-2.8
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
