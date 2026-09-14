# Admins Module — Identity, Platform Token (Bearer) & RBAC Reference

> As-built 2026-09-14. Source: `src/Api/Api/IdentityAccess/*.cs` (login + token), `src/Api/Api/Admins/*.cs`,
> `Program.cs` (routes), `CorsExtensions.cs`.
> สัญญาสำหรับทีม **admin console frontend** ที่ต่อกับ API นี้. แก้ auth/route/CORS เมื่อไหร่ update ไฟล์นี้ตามด้วย.
> ศัพท์/schema กลางดู [`ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md) ·
> [`rf1-schema-reset/design.md`](../../.ai/specs/rf1-schema-reset/design.md) (rename map เต็ม).
>
> ขอบเขต: เฉพาะ flow ของ admin console. merchant-user console เป็น **คนละกลไก**: ยังเป็น server-side OIDC BFF
> (cookie `__Host-mch_session` + `mch_csrf`, prefix `/api/v1/merchants/auth/{provider}/…`, scheme `MerchantUserMicrosoft`,
> config `MerchantAuth:Providers:*`, `MerchantSession:*`) ส่วน admin console ใช้ **platform JWT ใน `Authorization: Bearer`**
> ทางเดียว ไม่มี cookie/CSRF (legacy admin cookie stack ถูก retire 2026-09-14).
>
> **Microsoft-only OIDC:** ทั้งสอง plane รับเฉพาะ `microsoft` — Admin ใช้ workforce tenant (scheme
> `IdentityWorkforceMicrosoft`, config `IdentityAccess:Workforce:*`), merchant-user ใช้ CIAM tenant (scheme
> `MerchantUserMicrosoft`, config `MerchantAuth:Providers:Microsoft`). Google ถูก retire 2026-09-05: provider ที่ไม่ใช่
> Microsoft ที่ยังมี ClientId ฝั่ง merchant จะทำให้ boot guard throw นอก Development.

**Ports (dev):** API `https://localhost:5001` · Customer SPA `https://localhost:3000` · Admin Console
`https://localhost:3001` · Merchant-user Console `https://localhost:3002` (`Cors:AdminOrigins` /
`Cors:MerchantOrigins` ใน `appsettings.Development.json`)

**โมดูลในแผนที่แพลตฟอร์ม:** ดู [platform-modules.md](platform-modules.md) และ
[admin-control-plane.md](admin-control-plane.md) สำหรับ top-level admin operations.

## หลักการ (อ่านก่อนเขียนโค้ด)

Admin console มี credential เดียวคือ **employee platform token** (JWT) ที่ API ออกให้ผ่าน OpenIddict. SPA เป็น
**OpenIddict public client** (`client_id` = `IdentityAccess:WorkforceClientId`, default `pol-admin`) ใช้
Authorization Code + PKCE (S256). FE **ไม่** แตะ Microsoft โดยตรง และ **ไม่** ถือ Microsoft id_token — การยืนยันกับ
Entra ทำที่ API (confidential client) แล้ว API ออก token ของแพลตฟอร์มเองให้ SPA.

Flow login:

1. SPA นำ browser ไป (top-level navigation) ที่ `GET /oauth/authorize?client_id=pol-admin&response_type=code&redirect_uri=<origin>/auth/callback&code_challenge=…&code_challenge_method=S256&state=…`
2. API ไม่มี login cookie → challenge scheme `IdentityWorkforceMicrosoft` (Microsoft Entra workforce, tenant-pinned Authority,
   Authorization Code + PKCE + state + nonce ฝั่ง API)
3. ผู้ใช้ยืนยันกับ Microsoft → Microsoft redirect กลับ `/api/v1/admins/auth/microsoft/callback` (redirect URI ที่ register บน Entra app)
4. callback ตรวจ signature/issuer/audience/nonce/lifetime, บังคับ `tid`/`oid` อย่างละหนึ่งค่าและ exact tenant แล้ว
   resolve/JIT ด้วย exact tuple `(microsoft, tid, oid)` (`IdentityBffLoginService`) จากนั้น sign in cookie `pol_login`
   อายุ 2 นาที (scheme `identity-login`) แล้วส่ง browser กลับไป `/oauth/authorize` request เดิม
5. `/oauth/authorize` ออก authorization code แล้ว redirect ไป `<origin>/auth/callback?code=…&state=…`; cookie `pol_login` ถูกทิ้ง
6. SPA แลก code (+ `code_verifier`) ที่ `POST /oauth/token` ได้ **access JWT** (อายุ `IdentityAccess:AccessTokenMinutes` = 15 นาที) +
   **refresh token** (opaque, อายุ `IdentityAccess:RefreshTokenMinutes` = 480 นาที prod / 1440 dev, ออกใหม่อายุเต็มทุกครั้งที่ refresh
   → session slide ขณะใช้งาน)
7. ทุก API call แนบ `Authorization: Bearer <access JWT>`; ก่อนหมดอายุหรือเมื่อได้ 401 ให้ refresh (`grant_type=refresh_token`)

ไม่มี admin session cookie, ไม่มี CSRF header และไม่มี `credentials: 'include'` บน route admin อีกต่อไป.

## Platform token กับ Admin scope

route ที่ใช้ policy `admin` และ `dual-console` ผ่าน policy scheme `ConsoleSession` ซึ่ง forward audience Admin ไป scheme
`PlatformToken` (`src/Api/Api/IdentityAccess/PlatformTokenAuthentication.cs`): ห่อ OpenIddict validation (signature,
audience, lifetime, token/authorization entry) แล้วตรวจต่อ request ว่า account ยัง Active และ `authz_version` ใน token ตรงกับ
`AuthorizationVersion` ปัจจุบัน ถ้าไม่ตรง → 401 `invalid_token` (SPA refresh แล้ว version จะ re-sync). ไม่มี cookie fallback.

`IAdminScope` ถูก bind จาก authorization snapshot ของ Employee account: `AdminId` = `AccountId`, `permissions` = permission
ของ platform role, platform access = tier `Super` (เห็นทุก merchant) ส่วน employee ที่ไม่มี platform access เป็น `Scoped`
ตาม `MerchantAccess` ที่ Active. SYSTEM client และ account ที่ไม่ใช่ Employee ไม่ bind จึงเข้า admin console ไม่ได้.

Canonical commerce authorization ใช้ `Accounts.Domain.Account` และ `Access.Domain` แยกต่างหาก: `Employee` มี `PlatformAccess`,
`Agent` ผูก Sale/Branch owner, `System` ใช้ client assertion และ scope. `GET /api/v1/accounts...` กับ merchant/platform-access
routes จึงไม่ใช่ alias ของ `/api/v1/admins...` และไม่ควรใช้ `AdminId` เป็น `CreatedByAccountId` ใน canonical Order.

Order/transaction support ที่เปิดให้ Admin console ต้องตรวจ `IAdminScope` และ Account/Order parent ตาม endpoint; Admin tier ให้ขอบเขต
merchant ส่วน IAM permission/Account Access ให้ action และ business visibility.

Tier 0 ใช้ immutable tuple `Provider=microsoft`, validated tenant `tid` และ canonical directory object `oid`.
Email เป็น optional non-unique contact อาจ absent, mutable, reused หรือซ้ำกันได้ Runtime ไม่ fallback ไป Email,
UPN, `preferred_username`, `WorkforceEmailKey` หรือ `EmployeeId` และไม่อ่าน `roles` เพื่อให้สิทธิ์ Unknown exact tuple
สร้าง roleless Scoped JIT account Existing Admin ถูก offline-map หรือ pre-bound invite ก่อน login ไม่มี runtime bind
ด้วย Email.

## Token lifetime และ revocation

| รายการ | ค่า/พฤติกรรม |
|---|---|
| access JWT | 15 นาที (`IdentityAccess:AccessTokenMinutes`); authorization ถูก re-check จาก DB ทุก request |
| refresh token | 480 นาที prod, 1440 dev (`IdentityAccess:RefreshTokenMinutes`); ออกใหม่อายุเต็มทุกครั้งที่ refresh |
| logout | `POST /api/v1/auth/logout` (Bearer) revoke OpenIddict authorization ของ login นี้ → access + refresh ใช้ต่อไม่ได้ทันที |
| sessions ของตัวเอง | `GET /api/v1/me/sessions` (หนึ่งรายการต่อ login, ไม่มี token material) / `DELETE /api/v1/me/sessions/{sessionId}` |
| revoke token ของ admin คนอื่น | `POST /api/v1/accounts/{accountId}/session-revocations` (permission `user.manage`) bump `AuthorizationVersion` ของ account → token เดิมถูกปฏิเสธ 401 ที่ request ถัดไปเพราะ `authz_version` ไม่ตรง (ดู [`iam.md`](iam.md)) |

- `OAuth:Issuer` ต้อง pin เป็น public origin ของ API (dev `https://localhost:5001`): ถ้าไม่ pin OpenIddict derive issuer จาก host
  ของแต่ละ request → code/token ที่ออกผ่าน proxy ถูกปฏิเสธ (`invalid_grant`/401) เมื่อเรียกตรง และกลับกัน
- JavaScript ถือ token ได้ จึงมี XSS reach ที่ httpOnly cookie ไม่มี; ขอบที่คงไว้คือ PKCE, token/authorization entry validation,
  re-check `AuthorizationVersion` ทุก request และอายุ access token 15 นาที

## Proxy / origin

Bearer ไม่ผูกกับ origin จึง **ไม่บังคับ** same-origin proxy อีกต่อไป แต่ต้องคง 2 เงื่อนไข:

- `/oauth/authorize` เป็น top-level navigation (ไม่ต้อง CORS); `POST /oauth/token` และ `/oauth/revoke` ถูกเรียกจาก JavaScript
  → CORS policy dual-console ครอบให้แล้ว (`IsDualConsole` ใน `CorsExtensions.cs`) origin ของ SPA ต้องอยู่ใน `Cors__AdminOrigins`
- ทุก request ที่แตะ `/oauth/*` และ API ต้องไปถึง API ด้วย host เดียวกับ `OAuth:Issuer` (ผ่าน proxy ก็ได้ แต่ห้ามสลับ host ระหว่าง
  authorize/token/API call)

ถ้ายังใช้ Next.js proxy (`rewrites`) ให้ครอบ `/oauth/:path*`, `/api/v1/admins/:path*`, `/api/v1/merchants/:path*` และ area ของ
admin control plane (`/api/v1/{originators,payments,reports,approvals,audits,api-clients,webhooks,notifications}/:path*`)
เหมือนเดิม; backend honor `X-Forwarded-Host` (`UseForwardedHeaders`) แล้ว. เครื่อง dev ต้อง trust ASP.NET Core HTTPS
certificate (`dotnet dev-certs https --trust`) ก่อนให้ Next.js proxy ไป `:5001`; ถ้า Node.js ยังไม่อ่าน system CA ให้รัน frontend
ด้วย `NODE_OPTIONS=--use-system-ca`.

## Setup ฝั่ง FE

- **ไม่** ต้องขอ Microsoft token ใน browser และ **ไม่** ต้องโหลด MSAL/GIS script. Entra client id + secret เป็นของ server
  (confidential client, ฉีดผ่าน `IdentityAccess__Workforce__ClientId` / `IdentityAccess__Workforce__ClientSecret`)
- SPA ต้องรู้แค่ `client_id=pol-admin`, redirect URI `<origin>/auth/callback` (API register ให้ OpenIddict public client ตอน boot
  จาก `IdentityAccess:WorkforceWebAppBaseUrl`) และ PKCE
- ปุ่ม "Sign in with Microsoft" = สร้าง `code_verifier`/`state` แล้ว redirect (top-level) ไป `/oauth/authorize`
- หน้า `/auth/callback` แลก code ที่ `POST /oauth/token` (`application/x-www-form-urlencoded`) แล้วเก็บ access/refresh token ใน memory
  ของ SPA; ทุก API call แนบ `Authorization: Bearer`
- หน้า `/login-error?reason=<label>` รับ redirect เมื่อ login ที่ Entra/JIT ล้มเหลว (ดู Error model)
- admin SPA origin ต้องอยู่ใน `Cors__AdminOrigins` ฝั่ง server

```js
// เริ่ม login (PKCE)
const verifier = randomBase64Url(32); sessionStorage.setItem('pkce', verifier)
const challenge = base64Url(await sha256(verifier))
location.href = '/oauth/authorize?' + new URLSearchParams({
  client_id: 'pol-admin', response_type: 'code', redirect_uri: location.origin + '/auth/callback',
  code_challenge: challenge, code_challenge_method: 'S256', state: randomBase64Url(16),
})
```

## CSRF

**ไม่มี** บน route admin: Bearer header ไม่ถูก browser แนบข้าม site เอง จึงไม่ต้องมี double-submit token. CSRF ยังมีเฉพาะ
merchant-user console (`mch_csrf`) และ route `dual-console` จะบังคับ audience CSRF เฉพาะเมื่อ caller เป็น merchant cookie —
Bearer ผ่านโดยไม่ต้องส่งอะไรเพิ่ม.

## ขั้นแรกหลัง login: `GET /api/v1/admins/me`

หลัง SPA ได้ access token แล้ว ยิง `/api/v1/admins/me` (Bearer) เพื่ออ่าน identity/scope. First-login JIT server
ตรวจ workforce claims แล้วสร้าง `Active + Scoped` แบบไม่มี role/merchant assignment — FE ไม่ต้องส่งอะไรพิเศษ.

```js
async function bootstrap() {
  const res = await api('/api/v1/admins/me');
  if (res.status === 401) return login(location.pathname);  // token หมด/ถูก revoke และ refresh ไม่สำเร็จ -> re-login
  if (res.status === 403) return showNotActive();           // resolved แต่ suspended / ไม่ active
  renderNav(await res.json());                              // ใช้ tier + accessibleMerchants + permissions จัด UI
}
```

Response shape (`AdminMeResponse`, `src/Api/Api/Program.cs`):

```jsonc
// Super — เห็นทุก merchant; key `merchants` ถูก omit ทิ้งไปเลย (ไม่ใช่ null)
{
  "adminId": "…", "email": "a@x.com", "tier": "Super",
  "accessibleMerchants": { "isUnrestricted": true },
  "permissions": ["user.view", "user.manage", "…"]
}

// Scoped — เห็นเฉพาะ merchant ที่ถูก assign
{
  "adminId": "…", "email": null, "tier": "Scoped",
  "accessibleMerchants": { "isUnrestricted": false, "merchants": [ { "id": "…", "code": "acme" } ] },
  "permissions": ["user.view"]
}
```

`email` เป็น nullable contact และห้าม FE ใช้เป็น stable identity หรือ deduplication key

`tier` มี 2 ค่า: `"Super"` | `"Scoped"`. ใช้ตัดสินใจซ่อน/โชว์ action ที่เป็น Super-only; `permissions` = effective
action permission ของ role ที่ Active (admin-role-rbac REQ-9.1) — axis แยกจาก tier. `merchants[].code` เป็น
nullable (id ที่หา code ไม่เจอ -> `null`).

> **quirk ที่ต้องรู้ — `tier` casing ไม่ตรงกันข้าม endpoint**: `GET /me` (ข้างบน) กับ `POST /{id}/tier` (ดู
> [Account management](#account-management-spec-admin-account-management-scheme-apiv1admins)) คืน `tier` แบบ
> PascalCase (`"Super"`/`"Scoped"`, ผ่าน enum `.ToString()` ตรงๆ) — ในขณะที่ `GET /api/v1/admins` (list) และ
> `GET /api/v1/admins/{id}` (detail) คืนแบบ lowercase (`"super"`/`"scoped"`) เป็น quirk จริงในโค้ด ไม่ใช่เอกสารพิมพ์ผิด
> — FE ที่แชร์ renderer ระหว่าง `/me` กับ list/detail ต้อง normalize case เอง (เช่น `.toLowerCase()` ก่อนเทียบ).

> `GET /api/v1/admins/{id}` (detail) ใช้ **DTO ตัวเดียวกันและ JSON key เดียวกัน** (`accessibleMerchants`) โดยตั้งใจ
> ให้ client แชร์ renderer ตัวเดียวได้ (`AdminDetailResponse`) — detail คืน `roleCodes` และ version ของ Admin
> model. Org reference fields `position`/`office`/`level`/`division` เป็น historical surface ที่ถูก retire; employee
> profile ปัจจุบันอ่านจาก HR mirror ตาม identity adapter ไม่ได้อยู่ใน Admin API DTO.
>
> ```jsonc
> {
>   "adminId": "…", "email": "b@x.com", "tier": "scoped", "status": "active",
>   "createdAt": "…", "subjectBound": true,
>   "accessibleMerchants": { "isUnrestricted": false, "merchants": [ { "id": "…", "code": "acme" } ] },
>   "roleCodes": ["platform_auditor"]
> }
> ```

## Endpoints

auth = **`Authorization: Bearer <platform JWT>`** ทุก route. ไม่มี CSRF. Super-only = Scoped ยิงโดน 403.

| Method | Path | Tier | Body | Success | Note |
|---|---|---|---|---|---|
| GET | `/oauth/authorize` | — (anon) | query PKCE | 302 | เริ่ม login ของ SPA; ไม่มี `pol_login` → challenge Entra (scheme `IdentityWorkforceMicrosoft`) |
| POST | `/oauth/token` | — (anon) | form `authorization_code`+`code_verifier` / `refresh_token` | 200 | access JWT 15 นาที + refresh token; refresh รับ `merchant_id` เพื่อออก token ใน merchant context |
| POST | `/api/v1/auth/logout` | any | — | 204 | revoke OpenIddict authorization ของ token ปัจจุบัน (access + refresh ของ login นี้ตายทันที) |
| GET | `/api/v1/me/sessions` | any | — | 200 | login sessions ของตัวเอง (หนึ่งรายการต่อ login) |
| DELETE | `/api/v1/me/sessions/{sessionId}` | any | — | 204 | revoke login ที่เลือก; idempotent; ไม่ใช่ของตัวเอง -> 404 |
| GET | `/api/v1/admins/me` | any | — | 200 | bootstrap identity/scope |
| GET | `/api/v1/merchants/{code}` | any | — | 200 | scoped read; นอก scope/ไม่มี -> 404 |
| POST | `/api/v1/merchants` | **Super** | provision body | 201 | provision merchant (ดู reference 2.4); dup code -> 409 |
| POST | `/api/v1/admins` | **Super** | `{ "objectId": "…", "identityApprovalReference": "…", "email"?: "…" }` | 201 | pre-bound Microsoft Scoped admin; objectId จาก verified Entra export |
| POST | `/api/v1/admins/{id}/merchants` | **Super** | `{ "merchantId": "…" }` | 200 | assign merchant; inactive/unknown/dup -> 409 |
| DELETE | `/api/v1/admins/{id}/merchants/{merchantId}` | **Super** | — | 204 | unassign; unknown -> 404 |
| POST | `/api/v1/admins/{id}/suspend` | **Super** | — | 204 | suspend; suspend ตัวเอง -> 403 |

> route เดิม `GET /api/v1/admins/auth/{provider}/login`, `POST /api/v1/admins/auth/logout`, `POST /api/v1/admins/auth/logout-all`,
> `GET /api/v1/admins/{id}/sessions`, `DELETE /api/v1/admins/{id}/sessions/{sessionId}` ถูกลบ 2026-09-14 (404) พร้อม rate limiter
> `admin-auth`; revoke token ของ admin คนอื่นใช้ `POST /api/v1/accounts/{accountId}/session-revocations` (ดู
> [`iam.md`](iam.md)).
>
> **สองเส้นทาง merchant provisioning อยู่นอก prefix `/api/v1/admins`** (`hierarchical-naming` task 8): map ตรงบน
> `/api/v1/merchants` แล้ว re-attach control เองทีละ endpoint (policy `admin` + Super tier บน POST)
> แทนการ inherit จาก group — admin CORS policy ผูกให้ผ่าน path table ใน method `IsAdminPlane` ของ
> `src/Api/BuildingBlocks.Web/CorsExtensions.cs` (**ไม่ใช่** `Program.cs` ตามที่เอกสารรุ่นก่อนเขียนผิด).
> FE ยังยิงผ่าน proxy เดิมได้ แต่ rewrite rule ต้องครอบ `/api/v1/merchants` ด้วย ไม่ใช่แค่ `/api/v1/admins` (ดู
> [Proxy](#proxy--origin)).

### Account management (spec `admin-account-management`, scheme `/api/v1/admins`)

reads gate ด้วย permission `user.view` (single-key ไม่ใช่ tier); lifecycle ops gate ด้วย `Tier.Super`.
กติกา: role ที่ให้ `user.roles` ควร grant `user.view` ด้วย ให้ operator เห็น directory ก่อน assign role.

`POST /api/v1/admins` (invite, ตารางบน) รับ body `{ "objectId": "…", "identityApprovalReference": "…", "email"? }` — `objectId` และ approval reference เป็น required; Email เป็น optional contact. Employee HR profile ใช้ identity adapter/HR mirror ไม่ใช่ org-reference FK ใน Admin schema.

| Method | Path | Gate | Success | Note |
|---|---|---|---|---|
| GET | `/api/v1/admins` | `user.view` | 200 | SFS list: `page`/`limit`/`filters`(email/tier/status)/`sort`(email/createdAt)/`search`(email); tier/status ค่า lowercase, นอก domain -> 400 |
| GET | `/api/v1/admins/{id}` | `user.view` | 200 | detail: tier, status, `accessibleMerchants` (unrestricted ถ้า Super), `roleCodes` (รวม Inactive) และ `version` + header `ETag: "v<version>"` (ใช้เป็น `If-Match` ของ `PUT /{id}/roles`); unknown -> 404 |
| GET | `/api/v1/admins/{id}/effective-permissions` | `user.view` | 200 | union ของ role Active, sorted ascending; ใช้กับ suspended target ได้; unknown -> 404 |
| POST | `/api/v1/admins/{id}/tier` | **Super** | 200 | body `{ "tier": "super"\|"scoped" }` (response `tier` เป็น PascalCase — ดู quirk ด้านบน); เปลี่ยน tier ตัวเอง -> 403; idempotent ถ้า tier ตรงกับปัจจุบัน; tier ไม่รู้จัก -> 400; unknown -> 404 |
| POST | `/api/v1/admins/{id}/reactivate` | **Super** | 204 | คืน Active + bump `AuthorizationVersion`/`version` ของ admin record; idempotent; unknown -> 404 |

`adminId` / `id` / `merchantId` เป็น Guid. JSON body/field เป็น camelCase.

### Role & permission management (RBAC, scheme `/api/v1/admins`)

Permission catalog ล่าสุดมี **7 กลุ่ม / 25 keys** แบ่งเป็น Platform 5 กลุ่ม / 17 keys, Merchant 1 กลุ่ม / 5 keys
และ Shared 1 กลุ่ม / 3 keys. Platform groups คือ `txn`, `merchant`, `user`, `system`, `merchants.users`; Merchant group คือ
`roles`; Shared group คือ `payment` (สิทธิ์ commerce ที่ Tier 0 และ Tier 1 ใช้ร่วมกัน — assign ผ่าน role scope Shared
เช่น `merchant_staff` ให้ admin ได้). รายการและ seed grants อยู่ใน [`iam.md`](iam.md). Endpoint กลุ่มนี้ใช้ Platform keys
ตาม gate ของแต่ละ route; top-level admin operations ใช้ catalog เดียวกัน ดู
[`admin-control-plane.md`](admin-control-plane.md).

อ่าน (`GET /permissions`, `GET /roles`, `GET /roles/{code}`) เปิดให้ admin ที่ login แล้วทุกคน (ไม่ต้องมี
permission key เฉพาะ); เขียน (create/update/delete role, set role ของ admin) gate ด้วย `user.roles`.

| Method | Path | Gate | Success | Note |
|---|---|---|---|---|
| GET | `/api/v1/admins/permissions` | any admin | 200 | catalog: `groups[{key,label}]` + `permissions[{key,label,resource}]` |
| GET | `/api/v1/admins/roles` | any admin | 200 | **`PagedResult<RoleResponse>`** `{ items, page, limit, total }` (ไม่ใช่ array ตรง ๆ) SFS: `page`/`limit`/`filters`/`sort`/`search`; แต่ละ item มี `version` แต่ list ไม่ส่ง header `ETag` |
| GET | `/api/v1/admins/roles/{code}` | any admin | 200 | บทบาทเดียว + header `ETag: "v<version>"`; ไม่รู้จัก code -> 404 |
| POST | `/api/v1/admins/roles` | `user.roles` | 201 | คืน `ETag` ของ role ใหม่; รหัสซ้ำ -> 409; permission key นอก catalog -> 400 |
| PUT | `/api/v1/admins/roles/{code}` | `user.roles` | 200 | **ต้องส่ง `If-Match: "v<version>"`** ไม่ส่ง/รูปแบบผิด -> 400 `invalid_etag`; version ไม่ตรง -> 409 `state_conflict`; คืน `ETag` ใหม่; code (จาก route) แก้ไขไม่ได้; ปิดใช้งาน `platform_admin` -> 409 |
| DELETE | `/api/v1/admins/roles/{code}` | `user.roles` | 204 | **ต้องส่ง `If-Match`** (400/409 เหมือน PUT); บทบาทที่ยังมีผู้ใช้ผูกอยู่ลบไม่ได้ -> 409; `platform_admin` (seed anchor) ลบไม่ได้เสมอ -> 409 แม้ไม่มีใครผูกอยู่เลย |
| PUT | `/api/v1/admins/{id}/roles` | `user.roles` | 204 | **ต้องส่ง `If-Match: "v<version>"` ของ Admin** (จาก `ETag`/`version` ของ `GET /admins/{id}` ไม่ใช่ของ role) ไม่ส่ง -> 400 `invalid_etag`, stale -> 409 `state_conflict`; คืน `ETag` ใหม่บน 204; แทนที่ role ทั้งหมดของ admin นั้นด้วยชุดที่ระบุ; role code ไม่รู้จัก -> 400; unknown admin -> 404 |

`RoleResponse`: `{ code, name, description, color, status, permissions: string[], userCount, version }` — `status` เป็น
lowercase wire string เหมือน admin tier/status; `version` เป็นเลขเดียวกับใน `ETag` (`"v<version>"` เป็น strong ETag
มี double quote ครอบ) client ต้องเก็บจาก GET/POST/PUT ล่าสุดแล้วส่งกลับใน `If-Match` ตอน PUT/DELETE (SPA ใช้
`items[].version` จาก list ได้เพราะ list ไม่มี header).

### Admin control plane

Top-level routes สำหรับ merchant lifecycle, originator, PSP/routing, merchant users/roles, governance/audit,
API clients, webhook/notification delivery และ reporting ใช้ `PlatformToken` (Bearer) ตาม path แต่ไม่ mount ใต้
`/api/v1/admins`. Route, permission, `If-Match`, `Idempotency-Key`, one-time secret และ export limits อยู่ใน
[`admin-control-plane.md`](admin-control-plane.md).

### หมายเหตุ: endpoint อื่นใต้ prefix เดียวกัน แต่ไม่ใช่ของโมดูลนี้

route ต่อไปนี้ mount อยู่ใต้ `/api/v1/admins/*` (ผ่าน policy `admin` เดียวกัน) ด้วยเหตุผล
auth เท่านั้น — เป็น business action ของโมดูลอื่น เอกสารเต็มอยู่คนละที่ ไม่ copy รายละเอียดมาซ้ำที่นี่:

- `POST /api/v1/admins/merchants/users/{merchantUserId}/approve|reject` — admin อนุมัติ/ปฏิเสธ merchant-user สมัคร
  ใหม่ ดู [`merchants.md`](merchants.md) §8 (sequence diagram เต็ม)
- ไม่มี current policy-reference endpoint ใต้ `/api/v1/admins`; policy entity/report surface ถูก retire แล้ว.

## Logout

```js
async function logout() {
  await api('/api/v1/auth/logout', { method: 'POST' }); // revoke authorization ของ login นี้ (ทุก token ของ login นี้ตาย)
  clearTokens();                                          // ทิ้ง access/refresh ใน memory
  login();                                                // กลับไปหน้า sign-in
}
```

ออกจากทุกอุปกรณ์: list `GET /api/v1/me/sessions` แล้ว `DELETE /api/v1/me/sessions/{sessionId}` ทีละรายการ.

## Redirect หลัง login

ไม่มี `returnTo` และไม่มี allowlist ฝั่ง server แล้ว: `/oauth/authorize` redirect ได้เฉพาะ `redirect_uri` ที่ตรงกับที่ register ไว้กับ
OpenIddict public client (`<IdentityAccess:WorkforceWebAppBaseUrl>/auth/callback`) และ SPA เป็นคนพา user กลับหน้าเดิมเองผ่าน `state`
(หรือ `sessionStorage`). `AdminSession` config เหลือแค่ `WebAppBaseUrl` (origin ของ SPA สำหรับ host ใช้นอก auth) และ
`ScalarBaseUrl` (Scalar UI, Development เท่านั้น).

## Error model

ทุก error เป็น RFC7807 ProblemDetails — `Content-Type: application/problem+json`, มี `title` + `status`.

| Status | ความหมาย | FE ทำอะไร |
|---|---|---|
| 401 | ไม่มี/หมดอายุ/ถูก revoke Bearer, หรือ `authz_version` ไม่ตรง (สิทธิ์เปลี่ยน) | refresh ที่ `POST /oauth/token`; ถ้า refresh ได้ `invalid_grant` -> login ใหม่ที่ `/oauth/authorize` |
| 403 | token valid แต่: account suspended / ไม่ active / tier ไม่พอ / ไม่มี permission | "ไม่มีสิทธิ์" |
| 404 | merchant นอก scope หรือไม่มีจริง (กัน existence leak) | not-found |
| 409 | duplicate (code / assignment ซ้ำ) | conflict |
| 400 | body ผิด format | validation error |

> login ที่ล้มเหลวก่อนได้ code (protocol/state/code exchange/signature/issuer/audience/nonce/lifetime ผิด,
> `tid`/`oid` missing/duplicate/malformed, tenant mismatch, suspended หรือ eligibility denial) server redirect ไป
> `<IdentityAccess:WorkforceWebAppBaseUrl>/login-error?reason=<label>` (ไม่ใช่ JSON): `auth-failed` (remote/protocol failure),
> `access-denied` (user ยกเลิกที่ Entra) หรือ code ของ `IdentityAccessException` ที่แทน `_` ด้วย `-` เช่น
> `workforce-not-eligible`, `account-suspended` — reason ไม่มี claim, Email หรือ EmployeeId.

## helper รวม (adminApi.js)

```js
// lib/adminApi.js — token อยู่ใน memory ของ SPA; ไม่มี cookie/CSRF
let access = null, refresh = null

export function login() { /* สร้าง PKCE แล้ว redirect ไป /oauth/authorize (ดู Setup ฝั่ง FE) */ }

async function token(params) {
  const res = await fetch('/oauth/token', {
    method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: 'pol-admin', ...params }),
  })
  if (!res.ok) return false
  ;({ access_token: access, refresh_token: refresh } = await res.json())
  return true
}

export const exchangeCode = (code) =>
  token({ grant_type: 'authorization_code', code, code_verifier: sessionStorage.getItem('pkce'),
          redirect_uri: location.origin + '/auth/callback' })

export async function adminFetch(path, opts = {}, retried = false) {
  const res = await fetch(path, { ...opts, headers: { ...opts.headers, Authorization: 'Bearer ' + access } })
  if (res.status !== 401 || retried) return res
  if (refresh && await token({ grant_type: 'refresh_token', refresh_token: refresh })) return adminFetch(path, opts, true)
  login() // refresh ไม่ผ่าน -> re-login
  return res
}

export const logout = () => adminFetch('/api/v1/auth/logout', { method: 'POST' })
```

## ห้าม

- อย่าขอ Microsoft token/MSAL ใน browser หรือส่ง Microsoft id_token มาที่ API — API รับเฉพาะ platform JWT ที่ออกจาก `/oauth/token`
- อย่าเก็บ token ลง `localStorage`/cookie ที่ script อื่นอ่านได้; เก็บใน memory และ refresh ด้วย refresh token
- อย่าเรียก `/api/v1/admins/auth/microsoft/callback` ตรง ๆ หรือนำ authorize URL เก่ามาใช้ซ้ำ (code/state ใช้ครั้งเดียว)

## Dev / CORS

- API เดียว serve ทั้ง 2 console, **CORS แยก policy**: admin = `Cors__AdminOrigins` (dev `https://localhost:3001`),
  merchant-user = `Cors__MerchantOrigins` (dev `https://localhost:3002`, เป็น default policy, credentialed เพราะยังเป็น cookie).
  เลือก policy **ตาม path** ผ่าน `PolCorsPolicyProvider` ไม่ใช่ตาม origin. path table (`IsAdminPlane`) ครอบ admin-plane area
  อื่นด้วย (`/approvals`, `/audits`, `/originators`, `/products/documents`, `/orders/export`, `/api-clients`,
  `/notifications`, `/reports`) ไม่ใช่แค่ `/admins`/`/merchants`; `/oauth/token` และ `/oauth/revoke` อยู่ใน dual-console
  policy (ทั้งสอง SPA เรียกจาก JavaScript). prod ต้องตั้ง origin จริง — ไม่ตั้ง = block ทุก cross-origin
- admin XHR ส่ง `Authorization: Bearer` ไม่ต้อง `credentials: 'include'`
- backend dev ต้องใส่ Entra client id + secret จริงที่ `IdentityAccess__Workforce__ClientId` /
  `IdentityAccess__Workforce__ClientSecret` (user-secrets หรือ `.env`), tenant-pinned `IdentityAccess__Workforce__Authority`,
  `IdentityAccess__WorkforceTenantId`/`WorkforceIssuer`/`WorkforceAudience`, `IdentityAccess__WorkforceWebAppBaseUrl` และ
  `OAuth__Issuer=https://localhost:5001` ถึงจะ login จริงได้ (ดู [local-dev-run.md](../runbooks/local-dev-run.md) §7).
- bootstrap Super ไม่ใช้ external allowlist; promote corporate account ผ่าน admin management API ก่อน production.
- `WorkforceTenantBinding` ยังเป็น deployment singleton และ Authority ยัง pin tenant เดียว Triple identity index
  ไม่ใช่ multi-tenant admission; ต้องมี approved tenant registry/allowlist design ก่อนรับ tenant ที่สอง.
- OpenAPI document เปิดเฉพาะ Development (`/openapi/...`) — prod ไม่ publish; document `admin` โฆษณา security scheme
  `PlatformToken` (http bearer JWT)

**backend ทำให้แล้ว (FE ไม่ต้องแตะ):**
- CORS allow `https://localhost:3001`
- honor `X-Forwarded-Host` (`UseForwardedHeaders`)
- register OpenIddict public client `pol-admin` + redirect URI `<WorkforceWebAppBaseUrl>/auth/callback` ตอน boot
- Microsoft redirect URI registration ของ `/api/v1/admins/auth/microsoft/callback` (ฝั่ง ops/backend)

## prod

topology เดียวกัน; `OAuth__Issuer` ต้องเป็น public origin ของ API และ `IdentityAccess__WorkforceWebAppBaseUrl` เป็น origin ของ SPA
(prod compose ป้อนจาก `ADMIN_ENTRA_*` + `ADMIN_FRONTEND_ORIGIN` ดู [deploy-self-host.md](../runbooks/deploy-self-host.md) §5.2).
FE code ไม่ต้องเปลี่ยน.

## Source of truth

- employee login (Entra challenge, callback JIT, `pol_login`, login-error redirect): `src/Api/Api/IdentityAccess/IdentityAccessWiring.cs`
- OAuth endpoints (`/oauth/authorize`, `/oauth/token`, `/api/v1/auth/logout`, `/api/v1/me/sessions`):
  `src/Api/Api/IdentityAccess/IdentityAccessEndpoints.cs`; OpenIddict public client registration:
  `src/Api/Api/IdentityAccess/WorkforceClientRegistration.cs`
- Bearer scheme + `IAdminScope` binding: `src/Api/Api/IdentityAccess/PlatformTokenAuthentication.cs`; policy scheme
  `ConsoleSession`: `src/Api/Api/Iam/ConsoleSessionAuthentication.cs`; options: `src/Api/Api/IdentityAccess/IdentityAccessOptions.cs`
- production boot guard: `ProvisioningGuards.RequireWorkforceAdminProvider` ใน `src/Api/Api/Program.cs`
- admin console origins (`AdminSession:WebAppBaseUrl`/`ScalarBaseUrl`): `src/Api/Api/Admins/AuthOptions.cs`
- routes (`/api/v1/admins` group + `/api/v1/merchants` provisioning): `src/Api/Api/Program.cs`
- top-level admin control routes: `src/Api/Api/ControlPlane/AdminControlEndpoints.cs`,
  `src/Api/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs`
- governance/approval/audit: `src/Api/Api/Governance/GovernanceEndpoints.cs`
- API clients/secrets: `src/Api/Api/Iam/ApiClientEndpoints.cs`
- webhook/notification delivery: `src/Api/Api/Notifications/DeliveryEndpoints.cs`,
  `src/Api/Api/Webhooks/InboundWebhookEndpoints.cs`
- reporting/transaction projection: `src/Api/Api/Reporting/AdminReportingEndpoints.cs`
- OpenAPI audience documents: `src/Api/Api/OpenApiDocuments.cs`
- CORS split + path-based policy selection: `src/Api/BuildingBlocks.Web/CorsExtensions.cs`
- tier enum: `src/Domain/Modules/Admins.Domain/Users/Tier.cs` (CLR name `Tier` ไม่ใช่ `AdminTier` แล้ว)
- accessible-merchants value object: `src/Application/Modules/Admins.Application/Users/AccessibleMerchants.cs`
- canonical business identity/access: `src/Api/Api/IdentityAccess/CanonicalAccessEndpoints.cs`, `src/Application/Modules/Accounts.Application/IdentityAccessContracts.cs`, `src/Domain/Modules/Access.Domain/AccessModels.cs`
