# Admins Module — Identity, Session (OIDC BFF) & RBAC Reference

> As-built 2026-08-13. Source: `src/Hosts/Api/Admins/*.cs`, `Program.cs` (routes),
> `CorsExtensions.cs`.
> สัญญาสำหรับทีม **admin console frontend** ที่ต่อกับ API นี้. แก้ auth/route/CORS เมื่อไหร่ update ไฟล์นี้ตามด้วย.
> ศัพท์/schema กลางดู [`ARCHITECTURE.md`](../../.ai/shared/ARCHITECTURE.md) ·
> [`rf1-schema-reset/design.md`](../../.ai/specs/rf1-schema-reset/design.md) (rename map เต็ม).
>
> ขอบเขต: เฉพาะ flow ของ admin console. merchant-user console ใช้ **OIDC BFF แบบเดียวกันเป๊ะ** แล้ว
> (ไม่มี Google id-token Bearer และไม่มี tenant policy) — แต่เป็น **คนละ instance แยกขาด**: prefix
> `/api/v1/merchants/auth/{provider}/…`, scheme `MerchantUser{Provider}`, cookie `__Host-mch_session` + `mch_csrf`,
> config `MerchantAuth:Providers:*`. ไม่มี Bearer/`Authorization` header เหลือในระบบแล้ว.
>
> **multi-provider-oidc:** route เป็น provider-scoped แล้ว — `{provider}` ใน path ด้านล่างรับ `google` หรือ
> `microsoft` (Microsoft Entra ID, scheme `AdminMicrosoft`, config section `AdminAuth:Providers:Microsoft`).
> เอกสารนี้ตัวอย่างส่วนใหญ่ใช้ `google` ตาม scope เดิม — provider ที่ไม่รู้จัก/ไม่ได้ config -> 404.

**Ports (dev):** API `http://localhost:5100` · Admin Console `http://localhost:5200` · Merchant-user Console
`http://localhost:5300` (`Cors:AdminOrigins` / `Cors:AllowedOrigins` ใน `appsettings.Development.json`)

**โมดูลในแผนที่แพลตฟอร์ม:** ดู [platform-modules.md](platform-modules.md) และ
[admin-control-plane.md](admin-control-plane.md) สำหรับ top-level admin operations.

## หลักการ (อ่านก่อนเขียนโค้ด)

Admin auth เป็น **server-side OIDC BFF** (Backend-for-Frontend). FE **ไม่** แตะ Google โดยตรง, **ไม่** ถือ
id_token, **ไม่** แนบ Bearer header. แทนที่ด้วย **session cookie** ที่ server เป็นคนออกหลัง login กับ Google
ฝั่ง server.

Flow login:

1. FE นำ browser ไป (top-level navigation, **ไม่ใช่** XHR/fetch) ที่ `GET /api/v1/admins/auth/google/login?returnTo=<path>`
2. Server redirect ไป Google (Authorization Code + PKCE + state + nonce, scope `openid email`)
3. ผู้ใช้ยืนยันกับ Google -> Google redirect กลับมาที่ `/api/v1/admins/auth/google/callback` (server-side, ไม่มีหน้าให้ FE)
4. Server แลก code เป็น token, ตรวจ `email_verified` + hosted domain, resolve/bind/self-provision admin, แล้ว
   **set cookie**: `__Host-adm_session` (opaque, HttpOnly) + `adm_csrf` (JS-readable) → redirect กลับ `returnTo`
5. จากนั้นทุก XHR ส่ง cookie อัตโนมัติ (`credentials: 'include'`) + แนบ `X-CSRF-Token` บน method ที่เปลี่ยน state

ไม่มี id_token ใน browser, ไม่มี GIS script, ไม่มี `Authorization` header.

> **สำคัญสุด:** `returnTo` ต้องเป็น path เดียวกับ origin (relative, ขึ้นต้น `/`) และอยู่ใน allowlist ฝั่ง server
> (`AdminSession:ReturnUrlAllowlist`). ค่านอก allowlist จะถูกแทนด้วย default path (กัน open-redirect).

## Session + cookie

| Cookie | อ่านจาก JS ได้ | อายุ/พฤติกรรม |
|---|---|---|
| `__Host-adm_session` (dev-http: `adm_session`) | **ไม่** (HttpOnly) | opaque 256-bit; server เก็บแค่ SHA-256 hash; idle 24h, absolute 7d |
| `adm_csrf` | ได้ (ไม่ HttpOnly) | คู่กับ session; ใช้ทำ double-submit (ดูล่าง) |

- **Rotation:** server หมุน session cookie ให้เองเป็นระยะ (ทุก ~15m) ผ่าน `Set-Cookie` ใน response ปกติ —
  FE ไม่ต้องทำอะไร (browser เปลี่ยน cookie ให้). token เก่าใช้ได้ต่ออีกชั่วครู่ (grace) ระหว่าง request ที่ค้าง
- **Revocation ทันที:** suspend admin / logout-all / ตรวจพบการ replay token เก่า -> ทั้ง family ถูก revoke,
  request ถัดไป 401 ทันที (ไม่ต้องรอ token หมดอายุ)
- session เป็น `SameSite=Lax` (same-site deploy) หรือ `None; Secure` (cross-site) — ตั้งฝั่ง server

## Proxy — same-origin (บังคับ)

backend redirect หลัง login = path บน origin เดียว และ cookie ผูกกับ origin → SPA กับ API ต้องเป็น origin
เดียวกัน. ตั้ง Next.js proxy:

```js
// next.config.js
module.exports = {
  async rewrites() {
    return [
      { source: '/api/v1/admins/:path*', destination: 'http://localhost:5100/api/v1/admins/:path*' },
      // merchant provisioning ย้ายออกจาก prefix /admins แล้ว — ต้อง proxy เส้นนี้ด้วย (ดู Endpoints)
      { source: '/api/v1/merchants/:path*', destination: 'http://localhost:5100/api/v1/merchants/:path*' },
      // admin control plane: merchant, originator, PSP, routing, identity, governance, reporting, delivery
      { source: '/api/v1/originators/:path*', destination: 'http://localhost:5100/api/v1/originators/:path*' },
      { source: '/api/v1/payments/:path*', destination: 'http://localhost:5100/api/v1/payments/:path*' },
      { source: '/api/v1/reports/:path*', destination: 'http://localhost:5100/api/v1/reports/:path*' },
      { source: '/api/v1/approvals/:path*', destination: 'http://localhost:5100/api/v1/approvals/:path*' },
      { source: '/api/v1/audits/:path*', destination: 'http://localhost:5100/api/v1/audits/:path*' },
      { source: '/api/v1/api-clients/:path*', destination: 'http://localhost:5100/api/v1/api-clients/:path*' },
      { source: '/api/v1/webhooks/:path*', destination: 'http://localhost:5100/api/v1/webhooks/:path*' },
      { source: '/api/v1/notifications/:path*', destination: 'http://localhost:5100/api/v1/notifications/:path*' },
      // master-data reference lists (profile FK ของ admin) เป็น top-level area แยกของตัวเอง ไม่อยู่ใต้ /admins —
      // ไม่ proxy ด้วยจะโดน 404 จาก frontend server แทนที่จะถึง API (ดู Dev / CORS)
      { source: '/api/v1/positions/:path*', destination: 'http://localhost:5100/api/v1/positions/:path*' },
      { source: '/api/v1/offices/:path*', destination: 'http://localhost:5100/api/v1/offices/:path*' },
      { source: '/api/v1/levels/:path*', destination: 'http://localhost:5100/api/v1/levels/:path*' },
      { source: '/api/v1/divisions/:path*', destination: 'http://localhost:5100/api/v1/divisions/:path*' },
    ]
  },
}
```

Next.js rewrites ส่ง `X-Forwarded-Host` ให้ backend เอง — backend honor แล้ว (`UseForwardedHeaders`) ไม่ต้องทำเพิ่ม.

## Setup ฝั่ง FE

- **ไม่** ต้องขอ Google OAuth client เอง, **ไม่** ต้องโหลด GIS script. client id + secret เป็นของ server
  (confidential client, ฉีดผ่าน `AdminAuth__Providers__Google__ClientId` / `AdminAuth__Providers__Google__ClientSecret`)
- ปุ่ม "Sign in with Google" = ลิงก์/redirect ไป `/api/v1/admins/auth/google/login?returnTo=${encodeURIComponent(path)}`
  (top-level navigation — อย่าใช้ fetch; flow เด้งออกไป Google แล้วกลับมาที่ `returnTo`)
- ทุก API call ตั้ง `credentials: 'include'` (ตรงข้ามกับโมเดลเดิม — ตอนนี้ auth = cookie)
- admin SPA origin ต้องอยู่ใน `Cors__AdminOrigins` ฝั่ง server (เปิด `AllowCredentials` ให้เฉพาะ origin นี้)

```js
window.location.href = '/api/v1/admins/auth/google/login?returnTo=' + encodeURIComponent('/dashboard')
```

## CSRF (double-submit) — บังคับบน POST/PUT/PATCH/DELETE

ทุก request ที่เปลี่ยน state ไปยัง `/api/v1/admins/*` ต้องแนบ header `X-CSRF-Token` ที่ **ค่าตรงกับ cookie `adm_csrf`**
มิฉะนั้น **403**. GET/HEAD/OPTIONS ไม่ต้อง (login/callback ที่เป็น GET จึงผ่าน).

```js
const readCsrf = () =>
  document.cookie.split('; ').find(c => c.startsWith('adm_csrf='))?.split('=')[1] ?? '';

const api = (path, opts = {}) => fetch(path, {
  ...opts,
  credentials: 'include',                         // ส่ง session cookie (BFF) — จำเป็น
  headers: {
    'Content-Type': 'application/json',
    'X-CSRF-Token': readCsrf(),                   // double-submit; server เทียบกับ cookie adm_csrf
    ...opts.headers,
  },
});
```

(helper รวมที่พร้อมใช้จริง — auto CSRF เฉพาะ method ที่เปลี่ยน state + re-login on 401 — ดู [helper รวม](#helper-รวม-adminapijs) ด้านล่าง)

## ขั้นแรกหลัง login: `GET /api/v1/admins/me`

session cookie = httpOnly → JS อ่านไม่ได้ (ตั้งใจ กัน XSS). หลัง callback set cookie + redirect กลับ `returnTo`
แล้ว, FE ยิง `/api/v1/admins/me` (พร้อม `credentials: 'include'`) เพื่ออ่าน identity/scope. First-login binding (bind
invited Scoped by email / self-provision Super จาก allowlist) server จัดการตอน callback แล้ว — FE ไม่ต้องส่งอะไรพิเศษ.

```js
async function bootstrap() {
  const res = await api('/api/v1/admins/me');
  if (res.status === 401) return login(location.pathname);  // ไม่มี session / หมด / ถูก revoke -> re-login
  if (res.status === 403) return showNotActive();           // resolved แต่ suspended / ไม่ active
  renderNav(await res.json());                              // ใช้ tier + accessibleMerchants + permissions จัด UI
}
```

Response shape (`AdminMeResponse`, `src/Hosts/Api/Program.cs:2393-2395`):

```jsonc
// Super — เห็นทุก merchant; key `merchants` ถูก omit ทิ้งไปเลย (ไม่ใช่ null)
{
  "adminId": "…", "email": "a@x.com", "tier": "Super",
  "accessibleMerchants": { "isUnrestricted": true },
  "permissions": ["user.view", "user.manage", "…"]
}

// Scoped — เห็นเฉพาะ merchant ที่ถูก assign
{
  "adminId": "…", "email": "b@x.com", "tier": "Scoped",
  "accessibleMerchants": { "isUnrestricted": false, "merchants": [ { "id": "…", "code": "acme" } ] },
  "permissions": ["user.view"]
}
```

`tier` มี 2 ค่า: `"Super"` | `"Scoped"`. ใช้ตัดสินใจซ่อน/โชว์ action ที่เป็น Super-only; `permissions` = effective
action permission ของ role ที่ Active (admin-role-rbac REQ-9.1) — axis แยกจาก tier. `merchants[].code` เป็น
nullable (id ที่หา code ไม่เจอ -> `null`).

> **quirk ที่ต้องรู้ — `tier` casing ไม่ตรงกันข้าม endpoint**: `GET /me` (ข้างบน) กับ `POST /{id}/tier` (ดู
> [Account management](#account-management-spec-admin-account-management-scheme-apiv1admins)) คืน `tier` แบบ
> PascalCase (`"Super"`/`"Scoped"`, ผ่าน enum `.ToString()` ตรงๆ) — ในขณะที่ `GET /api/v1/admins` (list) และ
> `GET /api/v1/admins/{id}` (detail) คืนแบบ lowercase (`"super"`/`"scoped"`) เป็น quirk จริงในโค้ด ไม่ใช่เอกสารพิมพ์ผิด
> — FE ที่แชร์ renderer ระหว่าง `/me` กับ list/detail ต้อง normalize case เอง (เช่น `.toLowerCase()` ก่อนเทียบ).

> `GET /api/v1/admins/{id}` (detail) ใช้ **DTO ตัวเดียวกันและ JSON key เดียวกัน** (`accessibleMerchants`) โดยตั้งใจ
> ให้ client แชร์ renderer ตัวเดียวได้ (`AdminDetailResponse`, `Program.cs:2407-2410`) — นอกจากนี้ detail ยังมี
> `roleCodes` (รวม role ที่ Inactive) และ `position`/`office`/`level`/`division` (แต่ละตัว `{ "id", "code", "name" }`
> หรือ `null` ถ้าไม่ได้ตั้งค่า):
>
> ```jsonc
> {
>   "adminId": "…", "email": "b@x.com", "tier": "scoped", "status": "active",
>   "createdAt": "…", "subjectBound": true,
>   "accessibleMerchants": { "isUnrestricted": false, "merchants": [ { "id": "…", "code": "acme" } ] },
>   "roleCodes": ["platform_auditor"],
>   "position": { "id": "…", "code": "sales_rep", "name": "Sales Representative" },
>   "office": null, "level": null, "division": null
> }
> ```

## Endpoints

auth = **session cookie** (`credentials: 'include'`). method ที่เปลี่ยน state ต้องมี `X-CSRF-Token`. Super-only =
Scoped ยิงโดน 403.

| Method | Path | Tier | CSRF | Body | Success | Note |
|---|---|---|---|---|---|---|
| GET | `/api/v1/admins/auth/google/login` | — (anon) | — | — | 302 | redirect ไป Google; `?returnTo=<allowlisted path>`; rate-limited (ดูล่าง) -> 429 ถ้าเกิน |
| POST | `/api/v1/admins/auth/logout` | any | ต้อง | — | 204 | revoke session family ปัจจุบัน (อุปกรณ์นี้) + เคลียร์ cookie |
| POST | `/api/v1/admins/auth/logout-all` | any | ต้อง | — | 204 | revoke ทุก session ของ admin นี้ (ทุกอุปกรณ์) |
| GET | `/api/v1/admins/me` | any | — | — | 200 | bootstrap identity/scope |
| GET | `/api/v1/merchants/{code}` | any | — | — | 200 | scoped read; นอก scope/ไม่มี -> 404 |
| POST | `/api/v1/merchants` | **Super** | ต้อง | provision body | 201 | provision merchant (ดู reference 2.4); dup code -> 409 |
| POST | `/api/v1/admins` | **Super** | ต้อง | `{ "email": "…" }` | 201 | invite Scoped admin (bind ตอน login แรกของ invitee) |
| POST | `/api/v1/admins/{id}/merchants` | **Super** | ต้อง | `{ "merchantId": "…" }` | 200 | assign merchant; inactive/unknown/dup -> 409 |
| DELETE | `/api/v1/admins/{id}/merchants/{merchantId}` | **Super** | ต้อง | — | 204 | unassign; unknown -> 404 |
| POST | `/api/v1/admins/{id}/suspend` | **Super** | ต้อง | — | 204 | suspend; suspend ตัวเอง -> 403 |

> **Auth rate limiting**: `GET /auth/{provider}/login` (เท่านั้น — endpoint อื่นในตารางนี้ไม่มี) ผ่าน sliding
> window ต่อ source IP: 20 request / 60 วินาที (6 segments, ไม่ queue เกิน limit -> 429 ทันที) นโยบายชื่อ
> `admin-auth` (`src/Hosts/Api/Admins/AuthRateLimiting.cs`, ผูกที่ `Program.cs:1011`) กันสแปม login/probe callback
> จาก IP เดียว ไม่กระทบการ login ปกติที่ไม่ถี่.
>
> **สองเส้นทาง merchant provisioning อยู่นอก prefix `/api/v1/admins`** (`hierarchical-naming` task 8): map ตรงบน
> `/api/v1/merchants` แล้ว re-attach control เองทีละ endpoint (`CsrfFilter` + policy `admin` + Super tier บน POST)
> แทนการ inherit จาก group — admin CORS policy ผูกให้ผ่าน path table ใน method `IsAdminPlane` ของ
> `src/BuildingBlocks/BuildingBlocks.Web/CorsExtensions.cs:91-98` (**ไม่ใช่** `Program.cs` ตามที่เอกสารรุ่นก่อนเขียนผิด).
> FE ยังยิงผ่าน proxy เดิมได้ แต่ rewrite rule ต้องครอบ `/api/v1/merchants` ด้วย ไม่ใช่แค่ `/api/v1/admins` (ดู
> [Proxy](#proxy--same-origin-บังคับ)).

### Account management (spec `admin-account-management`, scheme `/api/v1/admins`)

reads gate ด้วย permission `user.view` (single-key ไม่ใช่ tier); lifecycle/session ops gate ด้วย `Tier.Super`.
กติกา: role ที่ให้ `user.roles` ควร grant `user.view` ด้วย ให้ operator เห็น directory ก่อน assign role.

`POST /api/v1/admins` (invite, ตารางบน) รับ body `{ "email": "…", "positionId"?, "officeId"?, "levelId"?,
"divisionId"? }` — 4 FK เป็น optional ตั้งได้ตั้งแต่ตอนเชิญ ไม่ต้องรอ `PUT .../profile` ทีหลัง.

| Method | Path | Gate | CSRF | Success | Note |
|---|---|---|---|---|---|
| GET | `/api/v1/admins` | `user.view` | — | 200 | SFS list: `page`/`limit`/`filters`(email/tier/status)/`sort`(email/createdAt)/`search`(email); tier/status ค่า lowercase, นอก domain -> 400 |
| GET | `/api/v1/admins/{id}` | `user.view` | — | 200 | detail: tier, status, `accessibleMerchants` (unrestricted ถ้า Super), `roleCodes` (รวม Inactive), profile FKs (ดู JSON ด้านบน); unknown -> 404 |
| GET | `/api/v1/admins/{id}/effective-permissions` | `user.view` | — | 200 | union ของ role Active, sorted ascending; ใช้กับ suspended target ได้; unknown -> 404 |
| POST | `/api/v1/admins/{id}/tier` | **Super** | ต้อง | 200 | body `{ "tier": "super"\|"scoped" }` (response `tier` เป็น PascalCase — ดู quirk ด้านบน); เปลี่ยน tier ตัวเอง -> 403; idempotent ถ้า tier ตรงกับปัจจุบัน; tier ไม่รู้จัก -> 400; unknown -> 404 |
| PUT | `/api/v1/admins/{id}/profile` | `user.manage` | ต้อง | 204 | body `{ "positionId"?, "officeId"?, "levelId"?, "divisionId"? }` (Guid, full-replace — `null` = ล้างค่า); FK ไม่รู้จัก/ไม่ active -> 400; unknown admin -> 404 |
| POST | `/api/v1/admins/{id}/reactivate` | **Super** | ต้อง | 204 | คืน Active + revoke session ทั้งหมดของ target (fresh-login); idempotent; unknown -> 404 |
| GET | `/api/v1/admins/{id}/sessions` | **Super** | — | 200 | sessions (ไม่มี token material) + `isLive`; unknown -> 404 |
| DELETE | `/api/v1/admins/{id}/sessions/{sessionId}` | **Super** | ต้อง | 204 | revoke ทั้ง rotation family; unknown/ไม่ใช่เจ้าของ -> 404; idempotent |

`adminId` / `id` / `merchantId` เป็น Guid. JSON body/field เป็น camelCase.

### Role & permission management (RBAC, scheme `/api/v1/admins`)

Permission catalog ล่าสุดมี **7 กลุ่ม / 26 keys** แบ่งเป็น Platform 5 กลุ่ม / 18 keys และ Merchant 2 กลุ่ม /
8 keys. Platform groups คือ `txn`, `merchant`, `user`, `system`, `merchants.users`; Merchant groups คือ
`payment`, `roles`. รายการและ seed grants อยู่ใน [`iam.md`](iam.md). Endpoint กลุ่มนี้ใช้ Platform keys
ตาม gate ของแต่ละ route; top-level admin operations ใช้ catalog เดียวกัน ดู
[`admin-control-plane.md`](admin-control-plane.md).

อ่าน (`GET /permissions`, `GET /roles`, `GET /roles/{code}`) เปิดให้ admin ที่ login แล้วทุกคน (ไม่ต้องมี
permission key เฉพาะ); เขียน (create/update/delete role, set role ของ admin) gate ด้วย `user.roles`.

| Method | Path | Gate | CSRF | Success | Note |
|---|---|---|---|---|---|
| GET | `/api/v1/admins/permissions` | any admin | — | 200 | catalog: `groups[{key,label}]` + `permissions[{key,label,resource}]` |
| GET | `/api/v1/admins/roles` | any admin | — | 200 | SFS list ของบทบาท พร้อม permissions + จำนวนผู้ใช้ที่ผูก (`userCount`) |
| GET | `/api/v1/admins/roles/{code}` | any admin | — | 200 | บทบาทเดียว; ไม่รู้จัก code -> 404 |
| POST | `/api/v1/admins/roles` | `user.roles` | ต้อง | 201 | รหัสซ้ำ -> 409; permission key นอก catalog -> 400 |
| PUT | `/api/v1/admins/roles/{code}` | `user.roles` | ต้อง | 200 | code (จาก route) แก้ไขไม่ได้; ปิดใช้งาน `platform_admin` -> 409 |
| DELETE | `/api/v1/admins/roles/{code}` | `user.roles` | ต้อง | 204 | บทบาทที่ยังมีผู้ใช้ผูกอยู่ลบไม่ได้ -> 409; `platform_admin` (seed anchor) ลบไม่ได้เสมอ -> 409 แม้ไม่มีใครผูกอยู่เลย |
| PUT | `/api/v1/admins/{id}/roles` | `user.roles` | ต้อง | 204 | แทนที่ role ทั้งหมดของ admin นั้นด้วยชุดที่ระบุ; role code ไม่รู้จัก -> 400; unknown admin -> 404 |

`RoleResponse`: `{ code, name, description, color, status, permissions: string[], userCount }` — `status` เป็น
lowercase wire string เหมือน admin tier/status.

### Admin control plane

Top-level routes สำหรับ merchant lifecycle, originator, PSP/routing, merchant users/roles, governance/audit,
API clients, webhook/notification delivery และ reporting ใช้ `AdminSession` + CSRF ตาม path แต่ไม่ mount ใต้
`/api/v1/admins`. Route, permission, `If-Match`, `Idempotency-Key`, one-time secret และ export limits อยู่ใน
[`admin-control-plane.md`](admin-control-plane.md).

### หมายเหตุ: endpoint อื่นใต้ prefix เดียวกัน แต่ไม่ใช่ของโมดูลนี้

route ต่อไปนี้ mount อยู่ใต้ `/api/v1/admins/*` (ผ่าน CSRF filter + policy `admin` เดียวกัน) ด้วยเหตุผล
auth เท่านั้น — เป็น business action ของโมดูลอื่น เอกสารเต็มอยู่คนละที่ ไม่ copy รายละเอียดมาซ้ำที่นี่:

- `POST /api/v1/admins/merchants/users/{merchantUserId}/approve|reject` — admin อนุมัติ/ปฏิเสธ merchant-user สมัคร
  ใหม่ ดู [`merchants.md`](merchants.md) §8 (sequence diagram เต็ม)
- ไม่มี current policy-reference endpoint ใต้ `/api/v1/admins`; policy entity/report surface ถูก retire แล้ว.

## Logout

```js
async function logout(all = false) {
  await api(`/api/v1/admins/auth/logout${all ? '-all' : ''}`, { method: 'POST' }); // CSRF + cookie แนบให้โดย api()
  login();                                                                  // กลับไปหน้า sign-in
}
```

## returnTo allowlist

หลัง login backend redirect ไปได้เฉพาะ path ที่อยู่ใน `AdminSession:ReturnUrlAllowlist` (กัน open-redirect);
path นอก list — และ absolute URL — ถูก fallback เป็น `AdminSession:DefaultReturnPath`.

**committed default = `["/"]` เท่านั้น** (conservative). route ปลายทางจริงของ FE ตั้งต่อ deployment:
- dev (`appsettings.Development.json:44-51`): `/`, `/main`, `/dashboard`, `/tenants`, `/scalar` (Scalar uses `AdminSession:ScalarBaseUrl=http://localhost:5100`; frontend paths use `SpaBaseUrl=http://localhost:5200`)
- staging/prod: env `AdminSession__ReturnUrlAllowlist__0=/`, `__1=/dashboard`, ... (ดู deploy runbook)

**สำคัญ:** helper ด้านล่าง default `returnTo='/dashboard'` → deployment นั้นต้องมี `/dashboard` ใน allowlist
ไม่งั้นถูกเด้งกลับ `/` (`DefaultReturnPath`). ขอ ops เพิ่ม route ที่ FE ใช้จริง.

## Error model

ทุก error เป็น RFC7807 ProblemDetails — `Content-Type: application/problem+json`, มี `title` + `status`.

| Status | ความหมาย | FE ทำอะไร |
|---|---|---|
| 401 | ไม่มี session cookie / session หมด/ถูก revoke / ตรวจพบ replay (reuse) | redirect ไป `/api/v1/admins/auth/google/login` |
| 403 | session valid แต่: account suspended / ไม่ active / tier ไม่พอ / **CSRF token หาย/ไม่ตรง** | "ไม่มีสิทธิ์" หรือ refresh CSRF |
| 404 | merchant นอก scope หรือไม่มีจริง (กัน existence leak) | not-found |
| 409 | duplicate (code / assignment ซ้ำ) | conflict |
| 400 | body ผิด format | validation error |

> callback ที่ login ไม่ผ่าน (state ผิด / `email_verified=false` / hosted-domain ไม่ตรง / ไม่ allowlist /
> suspended) server redirect ไป `AdminAuth:ErrorPath` พร้อม `?reason=<label>` (ไม่ใช่ JSON) — FE หน้า error
> อ่าน `reason` ได้.

## helper รวม (adminApi.js)

```js
// lib/adminApi.js
const cookie = (n) =>
  decodeURIComponent(document.cookie.match(new RegExp('(?:^|; )' + n + '=([^;]+)'))?.[1] ?? '')

export function login(returnTo = '/dashboard') {
  window.location.href = '/api/v1/admins/auth/google/login?returnTo=' + encodeURIComponent(returnTo)
}

export async function adminFetch(path, opts = {}) {
  const method = (opts.method ?? 'GET').toUpperCase()
  const headers = { ...opts.headers }
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers['X-CSRF-Token'] = cookie('adm_csrf')
  const res = await fetch(path, { ...opts, headers, credentials: 'include' })
  if (res.status === 401) login(location.pathname) // session หมด -> re-login
  return res
}

export const logout = () => adminFetch('/api/v1/admins/auth/logout', { method: 'POST' })
```

## ห้าม

- เลิกใช้ GIS SDK / id-token / `Authorization: Bearer` (ของเก่า)
- อย่าอ่าน/เก็บ session cookie เอง (httpOnly)
- อย่าเรียก API ข้าม origin ตรง — ต้องผ่าน proxy (ดู [Proxy](#proxy--same-origin-บังคับ))

## Dev / CORS

- API เดียว serve ทั้ง 2 console, **CORS แยก policy แต่ credentialed ทั้งคู่** (cookie XHR เหมือนกัน — ตั้งแต่
  merchant-user ย้ายมา BFF): admin = `Cors__AdminOrigins` (dev `http://localhost:5200`), merchant-user =
  `Cors__AllowedOrigins` (dev `http://localhost:5300`, เป็น default policy). เลือก policy **ตาม path** ผ่าน
  `PolCorsPolicyProvider` ไม่ใช่ตาม origin. path table (`IsAdminPlane`) ครอบ `/api/v1/positions`, `/offices`,
  `/levels`, `/divisions` ด้วย (4 master-data reference list ที่ profile FK อ้างถึง — ย้ายออกจาก `/admins`
  group เป็น top-level area ของตัวเองตั้งแต่ 2026-07-20, gate `user.manage` ทั้งหมด; บทบาทของแต่ละตาราง ดู
  [`entity-fields.md`](entity-fields.md)) ไม่ใช่แค่ `/admins`/`/merchants`. prod ต้องตั้ง origin จริง — ไม่ตั้ง = block ทุก cross-origin
- XHR **ต้อง** `credentials: 'include'` ทั้งสองฝั่ง ถึงจะส่ง session cookie
- dev-http (localhost http): cookie ถอด `Secure` + ใช้ชื่อไม่มี `__Host-` prefix อัตโนมัติ — FE อ่าน `adm_csrf`
  ได้เหมือนกัน
- backend dev ต้องใส่ OIDC client id + secret จริงที่ `AdminAuth__Providers__Google__ClientId` / `AdminAuth__Providers__Google__ClientSecret`
  (user-secrets) ถึงจะ login จริงได้; placeholder boot ได้แต่ login ไม่ผ่าน
- bootstrap Super admin คนแรก: backend ใส่ Google `sub` ที่ `AdminAllowlist__Subjects__0`
- OpenAPI document เปิดเฉพาะ Development (`/openapi/...`) — prod ไม่ publish

**backend ทำให้แล้ว (FE ไม่ต้องแตะ):**
- CORS allow `http://localhost:5200`
- honor `X-Forwarded-Host` → `redirect_uri` ออกมาเป็น origin ของ FE
- Google redirect URI registration (ฝั่ง ops/backend)

## prod

topology เดียวกัน (reverse proxy → same-origin), cookie เป็น `Secure` + `__Host-` อัตโนมัติบน https.
FE code ไม่ต้องเปลี่ยน (ยัง `credentials: 'include'` + อ่าน `adm_csrf` เหมือนเดิม).

## Source of truth

ไฟล์ทั้งหมดย้ายเข้าโฟลเดอร์ `src/Hosts/Api/Admins/` แล้ว (ตัดคำนำหน้า `Admin` ออกจากชื่อไฟล์ — prefix ซ้ำกับ
โฟลเดอร์):

- OIDC login + callback (challenge/establish session): `src/Hosts/Api/Admins/OidcAuthentication.cs`,
  `src/Hosts/Api/Admins/LoginService.cs`
- session auth + rotation/reuse/revocation: `src/Hosts/Api/Admins/SessionAuthenticationHandler.cs`,
  `src/Persistence/Persistence.ControlPlane/Admins/SessionStore.cs`
- cookies (session + CSRF): `src/Hosts/Api/Admins/SessionCookies.cs`; CSRF filter: `src/Hosts/Api/Admins/CsrfFilter.cs`
- auth rate limiting: `src/Hosts/Api/Admins/AuthRateLimiting.cs`
- routes (`/api/v1/admins` group + `/api/v1/merchants` provisioning): `src/Hosts/Api/Program.cs`
- top-level admin control routes: `src/Hosts/Api/ControlPlane/AdminControlEndpoints.cs`,
  `src/Hosts/Api/ControlPlane/AdminMerchantIdentityEndpoints.cs`
- governance/approval/audit: `src/Hosts/Api/Governance/GovernanceEndpoints.cs`
- API clients/secrets: `src/Hosts/Api/Iam/ApiClientEndpoints.cs`
- webhook/notification delivery: `src/Hosts/Api/Notifications/DeliveryEndpoints.cs`,
  `src/Hosts/Api/Webhooks/InboundWebhookEndpoints.cs`
- reporting/transaction projection: `src/Hosts/Api/Reporting/AdminReportingEndpoints.cs`
- OpenAPI audience documents: `src/Hosts/Api/OpenApiDocuments.cs`
- CORS split + path-based policy selection: `src/BuildingBlocks/BuildingBlocks.Web/CorsExtensions.cs`
- tier enum: `src/Modules/Admins/Admins.Domain/Users/Tier.cs` (CLR name `Tier` ไม่ใช่ `AdminTier` แล้ว)
- accessible-merchants value object: `src/Modules/Admins/Admins.Application/Users/AccessibleMerchants.cs`
