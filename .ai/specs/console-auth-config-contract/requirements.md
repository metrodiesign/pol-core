# Requirements: Console Auth Configuration Contract

> Status: approved 2026-08-18
> Notes:, amended 2026-08-18

## Overview

ระบบมี Admin Console และ Merchant Console ที่ใช้ server-side OIDC BFF คนละ session แต่ใช้ API host
เดียวกัน งานนี้ทำให้ชื่อ configuration contract สมมาตร ลดความผิดพลาดของ operator และคงการป้องกัน
open redirect, credentialed CORS และการแยก auth plane เดิมระหว่างช่วงเปลี่ยนผ่าน

## Canonical Contract

รายการต่อไปนี้คือ variables ที่เปลี่ยนหรือยืนยันในงานนี้ ไม่ใช่ provider inventory ทั้งหมด
Google และ provider อื่นยังใช้ contract เดิมใต้ `AdminAuth` และ `MerchantAuth`:

```text
AdminAuth__Providers__Microsoft__Authority
AdminAuth__Providers__Microsoft__ClientId
AdminAuth__Providers__Microsoft__ClientSecret
AdminAuth__Providers__Microsoft__CallbackPath
AdminSession__WebAppBaseUrl
AdminSession__ReturnUrlAllowlist__0
AdminSession__ReturnUrlAllowlist__1
Cors__AdminOrigins__0

MerchantAuth__Providers__Microsoft__Authority
MerchantAuth__Providers__Microsoft__ClientId
MerchantAuth__Providers__Microsoft__ClientSecret
MerchantAuth__Providers__Microsoft__CallbackPath
MerchantSession__WebAppBaseUrl
MerchantSession__ReturnUrlAllowlist__0
MerchantSession__ReturnUrlAllowlist__1
Cors__MerchantOrigins__0
```

Session section ต้องรักษา field เดิมทั้งหมด:

| Section | Fields |
|---|---|
| `AdminSession` | `IdleMinutes`, `AbsoluteHours`, `RotationMinutes`, `GraceSeconds`, `SameSite`, `PreAuthTtlMinutes`, `DefaultReturnPath`, `ReturnUrlAllowlist`, `WebAppBaseUrl`, `ScalarBaseUrl` |
| `MerchantSession` | `IdleMinutes`, `AbsoluteHours`, `RotationMinutes`, `GraceSeconds`, `SameSite`, `DefaultReturnPath`, `ReturnUrlAllowlist`, `WebAppBaseUrl` |
| `Cors` | `AdminOrigins`, `MerchantOrigins` |

## REQ-1: Canonical Configuration Names

**User Story:** ในฐานะ operator ฉันต้องการชื่อ config ที่สมมาตรตาม Admin และ Merchant เพื่อให้ตั้งค่า
แต่ละ console ได้โดยไม่ต้องจำโครงสร้างพิเศษคนละแบบ

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL bind Admin OIDC provider settings from `AdminAuth:Providers:{Provider}`.
- 1.2 THE SYSTEM SHALL bind Merchant OIDC provider settings from `MerchantAuth:Providers:{Provider}`.
- 1.3 THE SYSTEM SHALL bind Admin session settings from `AdminSession`.
- 1.4 THE SYSTEM SHALL bind Merchant session settings from `MerchantSession`.
- 1.5 THE SYSTEM SHALL bind Admin browser origins from `Cors:AdminOrigins`.
- 1.6 THE SYSTEM SHALL bind Merchant browser origins from `Cors:MerchantOrigins`.
- 1.7 THE SYSTEM SHALL expose the Admin browser application target as `AdminSession:WebAppBaseUrl`.
- 1.8 THE SYSTEM SHALL expose the Merchant browser application target as `MerchantSession:WebAppBaseUrl`.
- 1.9 THE SYSTEM SHALL preserve every session field listed in the Canonical Contract table under its canonical section.
- 1.10 THE SYSTEM SHALL keep `MerchantUser:Invitation` outside `MerchantSession`.
- 1.11 THE SYSTEM SHALL keep `AdminSession:ScalarBaseUrl` unchanged.
- 1.12 THE SYSTEM SHALL keep Google and every existing provider-specific setting under `AdminAuth` and `MerchantAuth` unchanged.

## REQ-2: Legacy Configuration Compatibility

**User Story:** ในฐานะ operator ที่ยังไม่ได้ rollout config ใหม่ ฉันต้องการให้ deployment เดิมทำงานต่อได้
หนึ่ง release เพื่อย้ายค่าโดยไม่เกิด outage

Legacy aliases ใน release นี้:

| Legacy | Canonical |
|---|---|
| `AdminSession:SpaBaseUrl` | `AdminSession:WebAppBaseUrl` |
| `MerchantUser:Session:{Field}` | `MerchantSession:{Field}` |
| `MerchantUser:Session:SpaBaseUrl` | `MerchantSession:WebAppBaseUrl` |
| `Cors:AllowedOrigins` | `Cors:MerchantOrigins` |

**Acceptance Criteria (EARS):**

- 2.1 WHEN only canonical settings are supplied THE SYSTEM SHALL use the canonical settings.
- 2.2 WHEN only legacy settings are supplied THE SYSTEM SHALL map them to the corresponding canonical options.
- 2.3 WHEN a legacy alias is supplied THE SYSTEM SHALL emit one deprecation warning for its key family during startup.
- 2.4 WHEN multiple configuration providers define the same canonical key THE SYSTEM SHALL preserve standard ASP.NET Core provider precedence.
- 2.5 WHEN multiple configuration providers define the same legacy key THE SYSTEM SHALL preserve standard ASP.NET Core provider precedence.
- 2.6 WHEN canonical and legacy settings produce equivalent normalized options THE SYSTEM SHALL use the canonical contract.
- 2.7 IF explicit operator-supplied canonical and legacy settings produce different normalized options THEN THE SYSTEM SHALL fail startup before accepting requests.
- 2.8 WHEN a deployment supplies a legacy override over a C# initializer or committed base-appsettings default THE SYSTEM SHALL apply the legacy override without reporting a false conflict.
- 2.9 IF a compatibility warning or conflict error is emitted THEN THE SYSTEM SHALL include configuration key names without configuration values.
- 2.10 THE SYSTEM SHALL keep legacy aliases until an explicit cleanup spec removes them after at least one tagged release containing this change.
- 2.11 THE SYSTEM SHALL compare URI options after normalizing scheme and host casing, default ports, and a trailing root slash.
- 2.12 THE SYSTEM SHALL compare return URL allowlists as order-independent sets using ordinal member comparison.
- 2.13 THE SYSTEM SHALL compare CORS origin lists as order-independent sets after origin normalization.
- 2.14 THE SYSTEM SHALL compare non-URI numeric and Boolean aliases after binding them to their target types and compare other string aliases ordinally.

## REQ-3: Startup Configuration Validation

**User Story:** ในฐานะ operator ฉันต้องการให้ config ผิดหยุดระบบตั้งแต่ startup เพื่อไม่ให้พบปัญหา auth,
redirect หรือ CORS หลังเปิดรับ traffic แล้ว

Merchant invitation delivery ถือว่า configured เมื่อ `MerchantUser:Invitation:Smtp:Host` ไม่ว่าง

**Acceptance Criteria (EARS):**

- 3.1 WHEN the API host starts THE SYSTEM SHALL resolve provider precedence, aliases, and conflicts before validating one effective canonical configuration snapshot and accepting requests.
- 3.2 WHEN `WebAppBaseUrl` is blank THE SYSTEM SHALL preserve relative redirects for a same-origin deployment.
- 3.3 IF a non-blank `WebAppBaseUrl` is not an absolute HTTP or HTTPS URI THEN THE SYSTEM SHALL fail startup.
- 3.4 IF a non-blank `WebAppBaseUrl` contains user information, a query, a fragment, or a path other than `/` THEN THE SYSTEM SHALL fail startup.
- 3.5 IF an environment other than Development configures an HTTP `WebAppBaseUrl` THEN THE SYSTEM SHALL fail startup.
- 3.6 IF Development configures an HTTP `WebAppBaseUrl` whose URI is not loopback THEN THE SYSTEM SHALL fail startup.
- 3.7 IF a `ReturnUrlAllowlist` entry does not start with exactly one `/` THEN THE SYSTEM SHALL fail startup.
- 3.8 IF a `ReturnUrlAllowlist` contains duplicate entries under ordinal comparison THEN THE SYSTEM SHALL fail startup.
- 3.9 IF `DefaultReturnPath` is absent from its corresponding `ReturnUrlAllowlist` THEN THE SYSTEM SHALL fail startup.
- 3.10 IF a configured CORS origin is not an absolute HTTP or HTTPS origin THEN THE SYSTEM SHALL fail startup.
- 3.11 IF a configured CORS origin contains user information, a query, a fragment, or a path other than `/` THEN THE SYSTEM SHALL fail startup.
- 3.12 IF a configured CORS origin contains a wildcard THEN THE SYSTEM SHALL fail startup.
- 3.13 IF an environment other than Development configures an HTTP CORS origin THEN THE SYSTEM SHALL fail startup.
- 3.14 IF Development configures an HTTP CORS origin whose URI is not loopback THEN THE SYSTEM SHALL fail startup.
- 3.15 IF a CORS origin list contains duplicates after origin normalization THEN THE SYSTEM SHALL fail startup.
- 3.16 WHEN a CORS origin list is empty THE SYSTEM SHALL keep that cross-origin policy deny-by-default.
- 3.17 IF startup validation fails THEN THE SYSTEM SHALL identify the canonical configuration key without emitting its value.
- 3.18 THE SYSTEM SHALL apply the same startup validation rules to effective values originating from canonical and legacy keys.
- 3.19 IF a `ReturnUrlAllowlist` entry contains a backslash or ASCII control character THEN THE SYSTEM SHALL fail startup.
- 3.20 WHEN Merchant invitation delivery is configured THE SYSTEM SHALL require a non-blank `MerchantSession:WebAppBaseUrl` before accepting requests.

## REQ-4: Redirect and Invitation Behavior

**User Story:** ในฐานะ Admin หรือ Merchant User ฉันต้องการกลับไปยังหน้าเว็บที่อนุญาตหลัง login
โดยไม่เปิดช่อง open redirect

**Acceptance Criteria (EARS):**

- 4.1 WHEN Admin login receives `returnTo` THE SYSTEM SHALL resolve it against `AdminSession:ReturnUrlAllowlist` using ordinal exact matching.
- 4.2 WHEN Merchant login receives `returnTo` THE SYSTEM SHALL resolve it against `MerchantSession:ReturnUrlAllowlist` using ordinal exact matching.
- 4.3 IF `returnTo` is empty, absolute, starts with `//`, or is absent from the applicable allowlist THEN THE SYSTEM SHALL use `DefaultReturnPath`.
- 4.4 WHEN an allowed Admin return path is relative and `AdminSession:WebAppBaseUrl` is configured THE SYSTEM SHALL return an absolute redirect under that base URL.
- 4.5 WHEN an allowed Merchant return path is relative and `MerchantSession:WebAppBaseUrl` is configured THE SYSTEM SHALL return an absolute redirect under that base URL.
- 4.6 WHEN `WebAppBaseUrl` is blank THE SYSTEM SHALL keep the resolved return path relative.
- 4.7 WHEN the Admin resolved path is `/scalar` and `ScalarBaseUrl` is configured THE SYSTEM SHALL preserve the existing Scalar redirect behavior.
- 4.8 WHEN Merchant authentication produces a registration redirect THE SYSTEM SHALL resolve the registration path under `MerchantSession:WebAppBaseUrl`.
- 4.9 WHEN the system generates a Merchant invitation link THE SYSTEM SHALL resolve the invitation path under `MerchantSession:WebAppBaseUrl`.

## REQ-5: CORS Policy Isolation

**User Story:** ในฐานะเจ้าของระบบ ฉันต้องการให้ browser ของแต่ละ console เรียก API ได้เฉพาะ plane
ที่กำหนด เพื่อไม่ให้การเปลี่ยนชื่อ config ลด isolation เดิม

**Acceptance Criteria (EARS):**

- 5.1 WHEN a browser request targets the Admin plane THE SYSTEM SHALL evaluate its origin against `Cors:AdminOrigins`.
- 5.2 WHEN a browser request targets the Merchant plane THE SYSTEM SHALL evaluate its origin against `Cors:MerchantOrigins`.
- 5.3 WHEN a browser request targets an existing dual-console endpoint THE SYSTEM SHALL evaluate its origin against the union of Admin and Merchant origins.
- 5.4 WHEN a configured origin is allowed THE SYSTEM SHALL return the matching `Access-Control-Allow-Origin` header.
- 5.5 WHEN a configured origin is allowed THE SYSTEM SHALL preserve credentialed CORS behavior.
- 5.6 IF an origin is absent from the selected policy THEN THE SYSTEM SHALL omit `Access-Control-Allow-Origin`.
- 5.7 THE SYSTEM SHALL preserve the existing API-path classification for Admin, Merchant, and dual-console CORS policies.
- 5.8 THE SYSTEM SHALL preserve authentication, authorization, and CSRF enforcement independently from CORS decisions.

## REQ-6: Authentication Compatibility Boundary

**User Story:** ในฐานะผู้ใช้ทั้งสอง console ฉันต้องการให้ config rename ไม่เปลี่ยน OIDC flow หรือ callback
ที่ลงทะเบียนไว้กับ identity provider

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL preserve every existing Admin and Merchant login route.
- 6.2 THE SYSTEM SHALL preserve every existing Admin and Merchant callback path.
- 6.3 THE SYSTEM SHALL preserve provider-scoped Authorization Code with PKCE behavior.
- 6.4 THE SYSTEM SHALL preserve confidential-client credential loading under `AdminAuth` and `MerchantAuth`.
- 6.5 THE SYSTEM SHALL preserve the separation between Admin and Merchant authentication schemes, cookies, and sessions.
- 6.6 THE SYSTEM SHALL require no database schema or data migration for this configuration change.

## REQ-7: Deployment Artifacts and Secret Safety

**User Story:** ในฐานะ operator ฉันต้องการตัวอย่างและ deployment manifests ตรงกับ runtime contract
เพื่อ copy ค่าได้โดยไม่เกิด silent fallback หรือ secret leak

**Acceptance Criteria (EARS):**

- 7.1 THE SYSTEM SHALL use canonical names in tracked runtime configuration files.
- 7.2 THE SYSTEM SHALL use canonical names in `docker-compose.prod.yml` while preserving its existing external input variable names.
- 7.3 THE SYSTEM SHALL use canonical names in `launchSettings.json`.
- 7.4 THE SYSTEM SHALL document canonical names in `.env.example` using blank or non-secret example values.
- 7.5 THE SYSTEM SHALL update current operator runbooks and current reference documentation to the canonical names.
- 7.6 THE SYSTEM SHALL leave historical approved specs unchanged except where they explicitly document this compatibility migration.
- 7.7 THE SYSTEM SHALL keep `.env` and all real credential values outside version control.
- 7.8 THE SYSTEM SHALL provide an old-to-new key mapping for operators migrating local, staging, and production configuration.
- 7.9 THE SYSTEM SHALL never log ClientSecret, token, password, private key, or complete configuration values during binding or validation.
- 7.10 THE SYSTEM SHALL NOT load `.env` files directly into the application process.
- 7.11 WHEN process configuration changes THE SYSTEM SHALL require an application restart before the new values become effective.
- 7.12 THE SYSTEM SHALL document that local execution must export `.env` values into the process environment before starting the API.

## REQ-8: Verification and Regression Coverage

**User Story:** ในฐานะ maintainer ฉันต้องการหลักฐานอัตโนมัติว่าชื่อใหม่, compatibility, validation และ
security behavior ทำงานจริงก่อน merge

**Acceptance Criteria (EARS):**

- 8.1 WHEN tests supply canonical settings only THE SYSTEM SHALL bind the expected Admin and Merchant option values.
- 8.2 WHEN tests supply legacy settings only THE SYSTEM SHALL bind the same effective option values and record a deprecation warning.
- 8.3 WHEN tests supply equivalent canonical and legacy settings THE SYSTEM SHALL start successfully using the canonical contract.
- 8.4 WHEN tests supply conflicting canonical and legacy settings THE SYSTEM SHALL fail startup without exposing either value.
- 8.5 WHEN tests exercise each invalid URL, return-path, and CORS-origin class from REQ-3 THE SYSTEM SHALL fail before serving requests.
- 8.6 WHEN tests exercise valid Development loopback HTTP and non-Development HTTPS configuration THE SYSTEM SHALL start successfully.
- 8.7 WHEN tests exercise Admin and Merchant login redirects THE SYSTEM SHALL preserve the allowlist and base-URL outcomes from REQ-4.
- 8.8 WHEN tests exercise Admin, Merchant, dual-console, and unknown origins THE SYSTEM SHALL preserve the CORS outcomes from REQ-5.
- 8.9 THE SYSTEM SHALL pin canonical configuration key literals in automated tests so an unreviewed rename fails the test suite.
- 8.10 THE SYSTEM SHALL pass restore, build with warnings as errors, affected tests, the full test suite, secret scan, and spec trace before implementation is marked complete.

## Out of Scope

- การเปลี่ยน Callback URI หรือ API route
- การเพิ่ม certificate หรือ private-key JWT client authentication
- การเปลี่ยน OIDC provider, session lifetime หรือ cookie policy
- การเปลี่ยน database table, column, migration หรือ persisted data
- การแก้ UI ของ Admin Console หรือ Merchant Console
- การแก้ค่าจริงใน `.env` ซึ่งเป็นไฟล์ local และ gitignored
- การลบ legacy aliases ใน release เดียวกับที่เพิ่ม canonical contract
- การเพิ่ม dotenv loader หรือ runtime configuration hot reload
- การบังคับ session/CORS validation ใน Worker ซึ่งไม่ใช้ config contract ชุดนี้

## Edge Cases & Open Questions

ไม่มี open question ค้างอยู่ การตัดสินใจยืนยันแล้ว:

- canonical names มี precedence เมื่อ canonical และ legacy ให้ผลเท่ากัน
- canonical/legacy conflict ทำให้ startup fail
- legacy-only deployment ใช้งานได้อย่างน้อยหนึ่ง tagged release พร้อม deprecation warning
- blank `WebAppBaseUrl` รองรับ same-origin deployment เมื่อ Merchant invitation ไม่ได้ configured
- empty CORS list คง fail-closed
- Development อนุญาต HTTP เฉพาะ loopback; environment อื่นบังคับ HTTPS
- historical specs เป็นบันทึกอดีตและไม่ถูก sweep rename

### Analysis Log: 2026-08-18

Anchor: repository HEAD `0ca847c` โดย `requirements.md` ยังเป็น untracked artifact ตอน audit
จึงยังไม่มี path-specific commit anchor

| Finding | Category | REQs | Decision | Rationale |
|---|---|---|---|---|
| AN-1 | Logical inconsistency | 2.7, 2.8 | Built-in defaults ไม่นับเป็น explicit conflict | legacy environment override ต้อง migrate ได้โดยไม่ชน default ของ repository |
| AN-2 | Ambiguity | 2.6, 2.11-2.14 | เทียบค่าหลัง normalize และเทียบ arrays แบบ set | ลำดับ array และรูป URI ที่สมมูลต้องไม่ทำให้ startup fail เทียม |
| AN-3 | Ambiguity | 2.10 | ลบ aliases ผ่าน cleanup spec หลังอย่างน้อยหนึ่ง tagged release | ไม่มี automatic removal ที่ผูกกับคำว่า release แบบกำกวม |
| AN-4 | Ambiguity | 1.1, 1.2, 1.12 | Canonical Contract เป็นรายการ variables ของงานนี้ | Google และ provider อื่นคง contract เดิม |
| AN-5 | Conflicting constraint | 3.2, 3.20, 4.9 | SMTP invitation ที่ configured ต้องมี Merchant WebApp base URL | email ต้องสร้าง absolute link และไม่มี request origin ให้ derive |
| AN-6 | Gap | 2.4-2.8, 3.1, 3.18 | resolve precedence และ aliases ก่อน validate canonical snapshot | legacy และ canonical ต้องผ่าน validation ชุดเดียวกัน |
| AN-7 | Gap | 3.7, 3.19, 4.3 | ปฏิเสธ backslash และ ASCII control characters | ปิด browser path-normalization bypass นอกเหนือจาก `//` |
| AN-8 | Unstated assumption | 3.1 | validate เฉพาะ API host | Worker ไม่ consume session หรือ CORS contract ชุดนี้ |
| AN-9 | Unstated assumption | 7.7, 7.10-7.12 | ไม่เพิ่ม dotenv loader และต้อง restart หลังเปลี่ยน config | ใช้ process environment/Compose ตาม runtime model เดิม |

ยังไม่มี `design.md` หรือ `tasks.md`; ไม่มี downstream artifact ที่ต้อง sync ในรอบนี้
