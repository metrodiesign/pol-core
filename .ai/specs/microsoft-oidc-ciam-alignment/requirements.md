# Requirements: Microsoft OIDC CIAM Alignment

> Status: approved 2026-08-16, amended 2026-08-16 (รอบสอง: B1/B3/H4 spec-architect critique; รอบสาม: R1-R5 design review; รอบสี่: P1-P2 design review — ทุกรอบ user สั่งแก้เอง = re-approve ในตัว, ดู findings log), amended 2026-08-17 (U1: user สั่งย้าย authority/tenant defaults ออกจาก appsettings ไป env ล้วน; U2: จาก code review — reconcile รูป authority ให้ตรงผล curl จริง: ตัวบังคับคือ suffix `/v2.0` รูป domain ใช้ได้)

## Overview

ผล audit (2026-08-16) พบว่า Microsoft OIDC ฝั่ง merchant ไม่ตรงเป้าหมายจริง: ระบบต้องใช้ Microsoft Entra External ID (CIAM, tenant `1aee3cad-1e4d-4de5-9e25-424d0d12520b` บน `viriyahexternal.ciamlogin.com`) แต่โค้ดปัจจุบัน config เป็น workforce `/organizations` (เปิดรับทุก Entra tenant) และ `MicrosoftOidc.ValidateIssuer` hardcode host `login.microsoftonline.com` ทำให้ CIAM token ไม่มีทางผ่าน validation ได้ (`src/Hosts/Api/OidcProviderOptions.cs:59`) — feature นี้แก้ให้ issuer validation ยึด discovery metadata ของ Authority ที่ config จริง (framework default), บังคับ tenant-pinned Authority ทั้งสอง plane (ตัด multi-tenant ออก), เพิ่ม provider discriminator ให้ identity mapping, แก้ invitation flow ที่ hardcode `google`, และเติม test ช่องว่างที่ audit ชี้ สอดคล้องหลัก deny-default และ identity isolation ของ platform (`.ai/shared/SECURITY_RULES.md`)

ค่า config ของ Microsoft provider ทุกตัว (authority, client id) inject ผ่าน env เท่านั้น — ไม่ commit ค่าใดใน appsettings/compose (amended U1 2026-08-17; เดิมให้ commit เป็น default); ClientSecret inject ตอน runtime ตาม pattern เดิม; blank ทั้งคู่ = provider ปิด

## REQ-1: Merchant Microsoft = Entra External ID (CIAM) single tenant

**User Story:** As a ตัวแทน (merchant user), I want login ด้วยบัญชี Entra External ID ของ `viriyahexternal`, so that เข้าระบบผ่าน identity provider ที่องค์กรกำหนดให้ Tier 1 ได้จริง

**Acceptance Criteria (EARS):**

- 1.1 THE SYSTEM SHALL รับ `MerchantAuth:Providers:Microsoft:Authority` ผ่าน env (`MERCHANT_ENTRA_AUTHORITY` -> `MerchantAuth__Providers__Microsoft__Authority`) โดย appsettings commit ค่าว่าง — ค่าที่ตั้งต้องลงท้าย `/v2.0` เสมอ รูปแนะนำคือ tenant-id path เช่น `https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0` (decision A3; amended U1; amended U2: รูป domain `.onmicrosoft.com/v2.0` ใช้ได้เช่นกัน — พิสูจน์ curl กับ tenant จริง 2026-08-17)
- 1.2 WHEN merchant user เรียก `GET /api/v1/merchants/auth/microsoft/login` (โดย ClientId ถูก config แล้ว) THE SYSTEM SHALL challenge ไปยัง authorization endpoint ที่ได้จาก discovery metadata ของ Authority ตาม 1.1
- 1.3 WHEN id_token จาก CIAM มี `iss` ตรงกับ `issuer` ใน discovery metadata ของ Authority ที่ config THE SYSTEM SHALL ยอมรับ issuer นั้น — ใช้ default issuer validation ของ framework (เทียบ metadata issuer) ห้าม hardcode host และห้ามใช้ custom `IssuerValidator`
- 1.4 IF id_token มี `iss` ไม่ตรงกับ issuer จาก discovery metadata ของ Authority THEN THE SYSTEM SHALL ปฏิเสธ token (`SecurityTokenInvalidIssuerException`) และจบที่ error redirect ตาม flow deny เดิม
  (เกณฑ์ 1.5 เดิมถอดออก — decision A1: Authority แบบ tenant-pinned ผูก tenant ไว้ใน issuer แล้ว การเทียบ `iss` กับ metadata issuer ตาม 1.3 ครอบการจำกัด tenant ในตัว ไม่มีเกณฑ์ `tid` แยก; เลข 1.5 เว้นไว้ไม่ reuse)
- 1.6 THE SYSTEM SHALL คงพฤติกรรม subject = `oid`, email fallback = `preferred_username` แบบมี `@`, และ `emailVerified: false` สำหรับ Microsoft ไว้ตามเดิม
- 1.7 THE SYSTEM SHALL ไม่ปิดหรือลดระดับ validation ใดของ framework (signature/JWKS, `aud`, lifetime, nonce, state)

## REQ-2: Admin Microsoft tenant pinning (Tier 0)

**User Story:** As a platform, I want admin plane ยอมรับเฉพาะ token จาก workforce tenant เดียวขององค์กร, so that ไม่มี identity นอก tenant `05ab044e-e2c5-47dc-bbfb-fd7ea077fa71` เข้าถึง admin console ได้

**Acceptance Criteria (EARS):**

- 2.1 THE SYSTEM SHALL รับ `AdminAuth:Providers:Microsoft:Authority` ผ่าน env (`ADMIN_ENTRA_AUTHORITY` -> `AdminAuth__Providers__Microsoft__Authority`) โดย appsettings commit ค่าว่าง (แทน placeholder `REPLACE_WITH_TENANT_ID` เดิม) — ค่าที่ตั้งต้องเป็นรูป tenant-id path `/v2.0` ของ workforce tenant (amended U1)
- 2.2 THE SYSTEM SHALL validate `iss` ของ id_token ฝั่ง admin กับ issuer จาก discovery metadata ของ Authority (default ของ framework — แนวเดียวกับ 1.3)
  (เกณฑ์ 2.3 เดิมถอดออก — decision A1: เหตุผลเดียวกับ 1.5; เลข 2.3 เว้นไว้ไม่ reuse)
- 2.4 WHERE `AllowedTenants` ถูกตั้งค่าไม่ว่าง THE SYSTEM SHALL บังคับ `tid` อยู่ใน allowlist เพิ่มจากเงื่อนไข issuer (gate ซ้อนแบบ optional ไม่ใช่ด่านหลัก)
- 2.5 THE SYSTEM SHALL ถอด `MicrosoftOidc.ValidateIssuer` (custom `IssuerValidator`) ออกจากทั้งสอง plane — issuer validation ทุก Microsoft scheme ใช้ default ของ framework (decision A2)

## REQ-3: บังคับ tenant-pinned Authority ระดับ boot + deployment

**User Story:** As a platform operator, I want ระบบ fail fast เมื่อ config เปิดกว้างเกิน, so that ไม่มี deployment ใดหลุดไปรับทุก Entra tenant โดยไม่ตั้งใจ

**Acceptance Criteria (EARS):**

- 3.1 IF section ใด (`AdminAuth` หรือ `MerchantAuth`) config Microsoft ด้วย multi-tenant Authority (`/common`, `/organizations`, `/consumers`) THEN THE SYSTEM SHALL fail fast ตอน boot นอก Development ไม่ว่า `AllowedTenants` จะตั้งไว้หรือไม่ (decision A2 + H4 — guard `RequireOidcProviders` คง scope non-Development เดิม)
- 3.2 THE SYSTEM SHALL เพิ่ม env mapping ใน `docker-compose.prod.yml` สำหรับ authority ทั้งสอง plane (`ADMIN_ENTRA_AUTHORITY`/`MERCHANT_ENTRA_AUTHORITY`, ไม่มี default ฝังใน compose — amended U1) เพื่อให้ prod กำหนดค่าจาก `.env` ล้วน
- 3.3 THE SYSTEM SHALL อัพเดท `.env.prod.example` ให้มี key ฝั่ง merchant ครบ (client id + authority) พร้อมคำอธิบายว่าค่าใดเป็น public / ค่าใดเป็น secret
- 3.4 THE SYSTEM SHALL คงพฤติกรรม blank ClientId = ข้าม scheme (ไม่ fault host) ไว้ตามเดิม

## REQ-4: Provider discriminator ใน identity mapping

**User Story:** As a platform, I want identity ถูก key ด้วยคู่ `(provider, subject)` ไม่ใช่ subject เดี่ยว, so that subject จาก provider หนึ่งไม่มีทางถูกตีความเป็น identity ของอีก provider

**Acceptance Criteria (EARS):**

- 4.1 THE SYSTEM SHALL resolve admin login ด้วยคู่ `(provider, subject)` — subject เดียวกันจากคนละ provider ต้องไม่ match กัน
- 4.2 THE SYSTEM SHALL resolve merchant user login ด้วยคู่ `(provider, subject)` แบบเดียวกัน
- 4.3 THE SYSTEM SHALL รองรับ `AdminAllowlist:Subjects` รูปแบบ `provider:subject` (เช่น `microsoft:<oid>`) — entry ที่ไม่มี prefix ตีความเป็น `google` เพื่อ backward compat กับค่าที่ตั้งอยู่แล้ว
- 4.4 WHEN admin self-provision ผ่าน allowlist THE SYSTEM SHALL ตรวจ provider ของ login ปัจจุบันตรงกับ prefix ของ entry ก่อนสร้าง Super
- 4.5 THE SYSTEM SHALL migrate ข้อมูล identity เดิม (แถวที่มี Subject อยู่แล้ว) ให้มี provider = `google` โดยไม่ทำให้ login เดิมหลุด
- 4.6 THE SYSTEM SHALL เพิ่ม `Provider` column บน `admin.Users` และ `merch.Users` พร้อม unique `(Provider, Subject)` (ฝั่ง admin คง filter `Subject IS NOT NULL` — invited admin ที่ยังไม่ bind มี Subject ว่าง) โดย**คงตาราง `merch.ExternalLogins` ไว้ตามเดิม** — critique B1 พิสูจน์ว่าตารางไม่ dead (registration เขียนทุกครั้งที่ `SubmitRegistration.cs:183`) การ drop = ถอด write path ทั้งชุด เกิน scope (supersede decision A4 เดิม)
- 4.7 THE SYSTEM SHALL เปลี่ยน admin operation routes บน merchant user (`/merchants/users/{subject}/approve|reject|registrations`) เป็น `{merchantUserId:guid}` และ lookup ด้วย `FindByIdAsync` เท่านั้น — review R1: dispatch เดิม `Guid.TryParse` แบบ subject-or-id ชนกับ Entra `oid` ที่เป็น GUID (subject ฝั่ง Microsoft จะถูกตีความเป็น internal id → 404 เสมอ) เป็น API contract change ที่ frontend admin console ต้องตามแก้
- 4.8 THE SYSTEM SHALL เพิ่ม `TargetUserId` ให้ `merch.RegistrationAudits` แล้วอ่าน/เขียน audit timeline ด้วย internal id แทน subject เดี่ยว — migrate แบบ nullable → backfill → fail ถ้ามีแถว unmatched → บังคับ `NOT NULL` + FK `Restrict` (review R3+P1: schema ใหม่ยอมให้ `(google, x)` และ `(microsoft, x)` อยู่พร้อมกัน อ่านด้วย subject จะรวม timeline สอง identity และรั่วข้าม accessible-scope; supersede L12 บางส่วน)
- 4.9 THE SYSTEM SHALL เพิ่ม `ActorAdminId Guid?` ให้ `merch.RegistrationAudits` — required สำหรับ action ที่มี admin actor (approve/reject/reveal/suspend), NULL เฉพาะ self-service registration; `ActorSubject`/`TargetSubject` เหลือไว้เพื่อ display เท่านั้น — review P1: `ActorSubject` เป็น actor key เดี่ยวที่ไม่ unique ข้าม provider แล้ว และ `ApproveCommand`/`RejectCommand` มี `ActingAdminId` อยู่แล้วแต่ audit ไม่เก็บ

## REQ-5: Invitation flow รองรับหลาย provider

**User Story:** As a merchant user ที่ได้รับ invitation, I want เลือก login ด้วย provider ที่องค์กรใช้ (Google หรือ Microsoft), so that รับ invitation ได้ไม่ว่า identity provider ใดถูกเปิดใช้

**Acceptance Criteria (EARS):**

- 5.1 WHEN `POST /api/v1/merchants/auth/invitations/start` ถูกเรียกพร้อม provider slug ที่ config แล้ว**และเป็น provider ที่ email verified** THE SYSTEM SHALL challenge ด้วย scheme ของ provider นั้น (แทน hardcode `google` ที่ `Program.cs:1970` ด้วย allowlist verified-email — ปัจจุบันมี `google` ตัวเดียว)
- 5.2 IF request ไม่ระบุ provider THEN THE SYSTEM SHALL ใช้ `google` เป็น default (backward compat กับ frontend ปัจจุบัน — decision A5)
- 5.3 IF provider ที่ระบุไม่ถูก config (scheme ถูก skip) THEN THE SYSTEM SHALL ตอบ 404 แบบเดียวกับ login endpoint
- 5.4 THE SYSTEM SHALL ส่ง `merchant_invitation_id` ผ่าน AuthenticationProperties เหมือนเดิมทุก provider ที่ผ่านเกณฑ์ 5.1
- 5.5 IF provider ที่ระบุถูก config แต่ email ไม่ verified (เช่น `microsoft`) THEN THE SYSTEM SHALL ตอบ 404 — critique B3: invitation จับคู่ด้วย email (`UserLoginService.cs:98-110`) ขณะ Entra email/`preferred_username` เป็น claim ที่ user แก้เองได้ = ช่องยกระดับสิทธิ์แบบเดียวกับที่ admin invite-bind ปิดไว้ (`LoginService.cs:40-47`) การเปิด Microsoft ต้องรอกลไก pre-bind `(provider, subject)` เป็น spec แยก

## REQ-6: Test coverage ช่องว่างจาก audit

**User Story:** As a maintainer, I want เส้นทาง auth ที่ attacker ควบคุม input ได้มี test ครอบ, so that regression ใน callback/error path ถูกจับก่อน merge

**Acceptance Criteria (EARS):**

- 6.1 THE SYSTEM SHALL มี test ที่ยิงผ่าน OIDC middleware จริง (callback path) อย่างน้อยหนึ่งเส้นต่อ provider ต่อ plane ครอบ: subject mapping (`oid` vs `sub`), `emailVerified` flag, tid gate ของ Microsoft, และเคส id_token ไม่มี email/`preferred_username` แบบมี `@` ฝั่ง merchant ต้อง deny ด้วย reason `missing-identity` (critique M8)
- 6.2 THE SYSTEM SHALL มี test ครอบ provider error path: `OnAccessDenied`, `OnRemoteFailure` (state mismatch), และ `MapFailureReason` ทุก branch (`email-unverified`, `hd-mismatch`, `tenant-missing`, `auth-failed`)
- 6.3 THE SYSTEM SHALL มี test issuer validation: CIAM issuer จาก discovery ผ่านฝั่ง merchant / issuer ต่าง tenant ถูกปฏิเสธ / workforce issuer tenant-pinned ผ่านฝั่ง admin
- 6.4 THE SYSTEM SHALL มี E2E test cross-plane: cookie `__Host-adm_session` ยิง merchant route ได้ 401 และ `__Host-mch_session` ยิง admin route ได้ 401
- 6.5 THE SYSTEM SHALL มี convention test บังคับว่าทุก mapped endpoint มี authorization metadata (`RequireAuthorization`/`RequirePermission`) หรือ `AllowAnonymous` อย่างชัดแจ้ง — พร้อม baseline allowlist ระบุ endpoint เดิมที่ยกเว้นโดยชอบธรรมพร้อมเหตุผลต่อรายการ, endpoint ใหม่นอก allowlist ที่ไม่มี metadata ทำให้ test fail (decision A6)
- 6.6 THE SYSTEM SHALL มี test ยืนยัน guard 3.1: multi-tenant Authority ถูกปฏิเสธตอน boot ทั้ง section `AdminAuth` และ `MerchantAuth` (รวมเคสที่ `AllowedTenants` ตั้งไว้ก็ยังถูกปฏิเสธ)
- 6.7 THE SYSTEM SHALL มี upgrade test ของ migration: seed identity เดิม (admin + merchant) บน schema ก่อนหน้า แล้ว migrate — ยืนยัน backfill `Provider='google'` และ login เดิมไม่หลุด (review R4: fresh-DB test อย่างเดียวไม่พิสูจน์ REQ-4.5)

## Edge Cases & Open Questions

- **Discovery ล่มตอน boot/refresh**: issuer จาก discovery ถูก cache โดย ConfigurationManager ของ framework — ถ้า authority ไม่ตอบตอน login จะ fail ที่ challenge/validation ตาม framework default (ไม่ต้อง handle เพิ่ม) — design ต้องยืนยันว่าไม่ได้ fetch discovery เองตอน boot จน host ตายเมื่อ network ขาด
- **CIAM discovery host ต่างจาก issuer host**: discovery จริงคืน issuer บน `{tenantId}.ciamlogin.com` ขณะ authority ใช้ `viriyahexternal.ciamlogin.com` — การ validate ต้องยึด issuer string จาก metadata ไม่ใช่ derive จาก authority URL เอง
- **AdminAllowlist entry เดิม**: ค่าปัจจุบันเป็น Google sub ไม่มี prefix — 4.3 ครอบแล้ว แต่ต้องมี test ยืนยันว่า login Google เดิมไม่หลุด
- **Session เดิมระหว่าง deploy**: การแก้ identity key (4.5) ต้องไม่ revoke session ที่ active อยู่ — session lookup ใช้ opaque token ไม่ผูก subject โดยตรง design ต้องยืนยัน
- **เปิด question (ยกเป็น pre-rollout gate — critique M8)**: app registration `fb0e40a7-...` ฝั่ง portal ต้องยืนยันเป็น client ของ CIAM tenant พร้อม redirect URIs (`https://localhost:5001/...`, `https://vcentralpaydev-api.viriyah.co.th/...`) และ **optional claim `email` ต้องถูกเปิด** — ถ้า CIAM ไม่ส่ง `email` และ `preferred_username` ไม่มี `@` ทุก login ฝั่ง merchant จะตายหลัง validate ผ่านด้วย reason `missing-identity` (hard-fail ที่ `UserLoginService.cs:92-95`) — ต้องยืนยันใน portal + E2E จริงก่อนประกาศเปิดใช้
- **เปิด question**: dev host จริงรัน `:5100` แต่ expected redirect ระบุ `:5001` — ต้อง confirm ว่า portal ลงทะเบียน URI ไหนให้ตรงกับ environment จริง

### Findings log (/spec-analyze 2026-08-16, anchor: fa48da0 — ไฟล์ยัง untracked ตอน analyze รอบแรก)

| # | ประเด็น | Decision | เหตุผล |
|---|---|---|---|
| A1 | REQ-1.5/2.3: วิธี derive "tenant ของ Authority" ไม่นิยาม | เลือก ก — ตัดเกณฑ์ tid แยกทิ้ง เทียบ `iss` กับ metadata issuer อย่างเดียว | tenant-pinned authority ผูก tenant ใน issuer แล้ว framework default ครอบในตัว |
| A2 | REQ-2 x 3.1: เส้น multi-tenant + AllowedTenants ไม่มีนิยาม issuer validation | เลือก ข — ตัด multi-tenant ออกทั้งระบบ บังคับ tenant-pinned | ไม่มีผู้ใช้จริง, ลบ custom validator ได้ทั้งก้อน, deny-default แข็งขึ้น |
| A3 | REQ-1.1: รูป authority CIAM (tenant-id vs domain) | เลือก ก — รูป tenant-id path | ตรง endpoint จริงจาก discovery ไม่ผูกชื่อ domain ที่เปลี่ยนได้ |
| A4 | REQ-4.1 vs 4.6: admin ไม่มี schema เทียบเท่า ExternalLogins | เลือก ก — `Provider` column สอง plane + unique `(Provider, Subject)` + drop `merch.ExternalLogins` | กลไกเดียวสมมาตร ไม่ maintain สองแบบ |
| A5 | REQ-5.2 vs 5.3: default `google` ชนเคส Google ถูกปิด | เลือก ก — คง default `google` | deployment จริงมี Google เสมอ เคสชนเป็น hypothetical |
| A6 | REQ-6.5: convention test blast radius กว้าง | เลือก ก — คลุมทุก endpoint + baseline allowlist | ปิด gap ทั้งระบบตามเจตนา audit โดยจุดเก่าไม่บล็อกงานนี้ |
| U1 | (2026-08-17, user หลัง implement) ค่า authority/tenant ถูก commit เป็น default ใน appsettings/compose | ย้ายเป็น env-inject ล้วน: appsettings ว่าง, compose passthrough `ADMIN_ENTRA_AUTHORITY`/`MERCHANT_ENTRA_AUTHORITY` ไม่มี default (supersede ส่วน "commit default" ของ A3/REQ-1.1/2.1/3.2) | user ต้องการเปลี่ยนค่าได้สะดวกโดยไม่แตะโค้ด; blank+blank = provider ปิด, guard tenant-pinned ยังบังคับตอนเปิดใช้ |
| U2 | (2026-08-17, spec review หลัง CI) `.env.prod.example` รับรองรูป domain `.onmicrosoft.com/v2.0` จากผล curl จริง แต่ design/REQ-1.1 ยังเขียนห้ามรูป domain — เอกสารขัดกัน | reconcile: ตัวบังคับคือ suffix `/v2.0` (ไม่มี = v1 metadata ทุกรูป) รูป domain + `/v2.0` ใช้ได้เท่ารูป tenant-id; tenant-id ยังเป็นรูปแนะนำ (เหตุผล A3 เดิม) | curl discovery กับ CIAM tenant จริง 2026-08-17: domain เปล่า = v1 issuer, domain + `/v2.0` = v2 ถูกต้อง — spec ต้องตรงพฤติกรรมจริง ไม่ใช่ตรงข้อสันนิษฐาน |

### Findings log (spec-architect critique 2026-08-16 บน design.md draft — amendment รอบสอง)

| # | ประเด็น | Decision | เหตุผล |
|---|---|---|---|
| B1 | `merch.ExternalLogins` ไม่ dead — registration เขียนทุกครั้ง (`SubmitRegistration.cs:183`) decision A4 ฐานผิด | เก็บตารางไว้ + Provider column บน Users อย่างเดียว (supersede A4) | diff เล็กสุด ไม่แตะ registration path ที่ทำงานอยู่ ยอมมีข้อมูล provider 2 ที่ |
| B2 | unique `(Provider, Subject)` ฝั่ง admin ชนแถว invited (Subject NULL) | คง filter `Subject IS NOT NULL` — แก้ใน 4.6 | admin invite คนที่สองต้องไม่พัง |
| B3 | เปิด invitation ให้ Microsoft = privilege escalation ผ่าน mutable email | จำกัด verified-email provider (google) — เพิ่ม 5.5 | เหตุผลเดียวกับ admin invite-bind fail-closed; pre-bind subject เป็น spec แยก |
| H4 | guard รันเฉพาะนอก Development — "เสมอ" ใน 3.1 เกินจริง | คง scope เดิม แก้ถ้อยคำ 3.1 | dev box ยังไม่ config ต้อง boot ได้; test 6.6 เรียก guard ตรงตาม pattern เดิม |
| M5 | กลไก issuer validation ใน design อ้าง path เก่า (.NET 10 ใช้ `JsonWebTokenHandler` + ConfigurationManager) | แก้ prose ใน design — ข้อสรุปเดิมถูก | กัน "แก้กลับ" ทีหลัง + test ต้องใช้ issuer literal ไม่ใช่ template |
| M6 | config ประกาศ 2 ที่ต่อ plane (migration-owner + runtime mirror) | ระบุครบ 4 ไฟล์ใน design | trap เดิมของ repo (2 SessionConfiguration) |
| M7 | ripple provider slug: merchant callback มี provider แล้ว, admin ยังไม่มี, ชั้น port/write site ขาด | แก้ ripple list ใน design ครบ | — |
| M8 | CIAM email dependency เป็น hard-fail หลัง validate | ยกเป็น pre-rollout gate + test ใน 6.1 | — |
| L9 | invitation slug ไม่ normalize case | `ToLowerInvariant()` ก่อน lookup | — |
| L10 | บ้านใหม่ของ constants `ExternalLogin.Google/.Microsoft` | ไม่ต้องย้าย — ตารางอยู่ต่อ (ตาม B1) | — |
| L11 | `AdminResolveLoginBySubject` port ไม่มี caller | แก้ signature ตาม `(Provider, Subject)` ให้สอดคล้อง | กันกับดักคน wire ทีหลัง |
| L12 | `RegistrationAudits.TargetSubject` ไม่มี provider | ยอมรับเป็นข้อจำกัด บันทึกใน design | collision ในทางปฏิบัติแทบเป็นศูนย์ (oid GUID vs Google sub ตัวเลข) |

### Findings log (design review รอบสอง 2026-08-16 — 4 High + 1 Medium, ทุกข้อ verify กับโค้ดแล้ว)

| # | ประเด็น | Decision | เหตุผล |
|---|---|---|---|
| R1 | route `{subject}` + `Guid.TryParse` dual dispatch ชน Entra `oid` GUID (`ApproveReject.cs:148`, `GetRegistrationHistory.cs:73`, `Program.cs:2511,2546,2572`) | เปลี่ยน 3 route เป็น `{merchantUserId:guid}` + `FindByIdAsync` เท่านั้น — เพิ่ม 4.7 | Microsoft subject ถูกกินเป็น id → 404; contract แบบ id มีใน list/detail อยู่แล้ว |
| R2 | convention test baseline ไม่แยก HTTP method | key = (method, route pattern) ผ่าน `IHttpMethodMetadata` | path เดียวหลาย method มีจริง (`/orders` GET+POST) |
| R3 | audit timeline อ่านด้วย `account.Subject` เดี่ยว (`GetRegistrationHistory.cs:86`) | เพิ่ม `TargetUserId` + backfill + อ่านด้วย internal id — เพิ่ม 4.8 (supersede L12 บางส่วน) | กัน timeline สอง identity ปน + audit รั่วข้าม scope |
| R4 | `Down()` คืน unique `Subject` ไม่ได้เมื่อมี subject ซ้ำต่าง provider | ประกาศ one-way point + rollback preflight + upgrade test — เพิ่ม 6.7 | rollback จริงต้อง forward-fix/restore backup |
| R5 | discovery ล่มตอน challenge ไม่เข้า `OnRemoteFailure` (.NET 10 await `GetConfigurationAsync` ตรง) | แก้ design ระบุ 5xx ตามจริง ไม่เพิ่ม handling | outage ชั่วคราว = 5xx ยอมรับได้ ไม่คุ้มเพิ่มโค้ด |

### Findings log (design review รอบสาม 2026-08-16 — 4 High + 1 Medium; R2/R5 ปิดแล้วจากรอบก่อน)

| # | ประเด็น | Decision | เหตุผล |
|---|---|---|---|
| P1-1 | design gate ปฏิเสธ token ไม่มี `tid` เสมอ ขัด REQ-2.4 (gate เฉพาะเมื่อ `AllowedTenants` ไม่ว่าง) | ครอบทั้ง missing/outside checks ด้วย `AllowedTenants.Length > 0` + test 3 เคส | pinned authority + empty allowlist ต้อง login ได้ตาม contract — tenant isolation มาจาก issuer แล้ว |
| P1-2 | `TargetUserId` nullable + `ActorSubject` ยังเป็น actor key เดี่ยว | NOT NULL + FK Restrict หลัง backfill (fail ถ้า unmatched) + เพิ่ม `ActorAdminId Guid?` — amend 4.8, เพิ่ม 4.9 | audit ต้องระบุ target/actor ได้เสมอหลัง subject ไม่ unique |
| P1-3 | rollback preflight เป็น prose ไม่ executable + admin duplicate check ไม่ตัด Subject NULL | `IF EXISTS ... THROW` ต้น `Down()` ทั้งสองตาราง (admin filter `Subject IS NOT NULL`), reverse `TargetUserId` ด้วย, test `Up → Down → Up` + duplicate-block; prod = restore backup เท่านั้นตาม runbook | comment ไม่กันการเรียก `Down()` ตรง |
| P1-4 | route change ไม่มี deployment sequence — backend-first ทำ client เดิม 404 | rollout 2 phase: FE ส่ง `merchantUserId` ก่อน (backend ปัจจุบันรับ GUID อยู่แล้วผ่าน TryParse) → แล้ว backend จำกัด `:guid` + commands เปลี่ยนเป็น `Guid MerchantUserId` + อัพเดท docs/contract tests | admin SPA กับ API deploy แยกกัน |
| P2-5 | dead subject-only seams: `IUserRepository.FindBySubjectAsync` (ไม่มี caller หลัง R1) + `AdminResolveLoginBySubject` (ไม่มี caller อยู่แล้ว) | ลบทั้งสอง seams (supersede L11 ที่เดิมเลือก "แก้ตาม") | เก็บ composite lookup เฉพาะ port ที่มี caller จริง |
