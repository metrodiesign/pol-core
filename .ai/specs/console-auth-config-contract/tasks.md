# Implementation Tasks: Console Auth Configuration Contract

> Status: approved 2026-08-18

> Each task is a cohesive, independently verifiable slice. Implement a whole task
> in one pass; decompose micro-steps only during execution.

Scope guard: ไม่เปลี่ยน OIDC route/callback/scheme, cookie contract, REST/OpenAPI, database, UI หรือไฟล์ `.env`

- [x] 1. สร้าง canonical configuration snapshot และ compatibility resolver — bind canonical Admin/Merchant session กับ CORS, merge legacy aliases ราย field ตาม provider precedence, normalize และตรวจ conflict ด้วย unit tests ครบถ้วน
     REQ-1 (all criteria), REQ-2.1-REQ-2.2, REQ-2.4-REQ-2.8, REQ-2.10-REQ-2.14, REQ-8.1, REQ-8.3.

- [x] 2. บังคับ startup validation และ deprecation reporting — wire snapshot เข้า API host แบบ lazy-until-startup, fail ก่อนรับ request, log key-family warning ครั้งเดียว และทดสอบ validation matrix กับ provider stack จริง
     REQ-2.3, REQ-2.7, REQ-2.9, REQ-3 (all criteria), REQ-8.2, REQ-8.4-REQ-8.6. Depends on: 1.

- [x] 3. ย้าย runtime consumers ไป canonical snapshot โดยคง auth behavior — update Admin/Merchant redirect, registration, invitation และ typed CORS policies พร้อม regression tests สำหรับ allowlist, Scalar, plane isolation, credentials, callbacks, schemes และ cookies เดิม
     REQ-4 (all criteria), REQ-5 (all criteria), REQ-6 (all criteria), REQ-8.7-REQ-8.8. Depends on: 1, 2.

- [x] 4. ย้าย tracked configuration และ operator documentation — ใช้ canonical keys ใน appsettings examples, launch settings, Compose, `.env.example` และ current docs โดยคง Compose input names, migration map, secret rules และ historical specs พร้อม contract-pin tests
     REQ-7 (all criteria), REQ-8.9. Depends on: 1.

- [x] 5. ประกอบและพิสูจน์ release gate — รัน canonical/legacy regression, build warnings-as-errors, non-integration และ SQL integration suites, rename contract check, secret scan และ spec trace จนครบโดยไม่มี uncovered REQ
     REQ-8.10. Depends on: 1, 2, 3, 4.
      - viewports: n/a — legacy corpus predates viewport protocol (human checkpoint 2026-08-26)
