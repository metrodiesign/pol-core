# Admin Google SSO (BFF) — Frontend Integration Contract

> Generated 2026-06-24 from `AdminOidcAuthentication.cs`, `AdminLoginService.cs`,
> `AdminSessionAuthenticationHandler.cs`, `AdminSessionCookies.cs`, `AdminCsrfFilter.cs`, `Program.cs` (routes) +
> `CorsExtensions.cs`. สัญญาสำหรับทีม **admin SPA frontend** ที่ต่อกับ API นี้. แก้ auth/route/CORS เมื่อไหร่ update
> ไฟล์นี้ตามด้วย.
>
> ขอบเขต: เฉพาะ flow ของ admin console. tenant SPA ยังใช้ Google id-token เป็น Bearer (audience `tenant`) —
> คนละ contract, ไม่เปลี่ยน.

## หลักการ (อ่านก่อนเขียนโค้ด)

Admin auth เป็น **server-side OIDC BFF** (Backend-for-Frontend). FE **ไม่** แตะ Google โดยตรง, **ไม่** ถือ
id_token, **ไม่** แนบ Bearer header. แทนที่ด้วย **session cookie** ที่ server เป็นคนออกหลัง login กับ Google
ฝั่ง server.

Flow login:

1. FE นำ browser ไป (top-level navigation, **ไม่ใช่** XHR/fetch) ที่ `GET /admin/auth/login?returnTo=<path>`
2. Server redirect ไป Google (Authorization Code + PKCE + state + nonce, scope `openid email`)
3. ผู้ใช้ยืนยันกับ Google -> Google redirect กลับมาที่ `/admin/auth/callback` (server-side, ไม่มีหน้าให้ FE)
4. Server แลก code เป็น token, ตรวจ `email_verified` + hosted domain, resolve/bind/self-provision admin, แล้ว
   **set cookie**: `__Host-adm_session` (opaque, HttpOnly) + `adm_csrf` (JS-readable) → redirect กลับ `returnTo`
5. จากนั้นทุก XHR ส่ง cookie อัตโนมัติ (`credentials: 'include'`) + แนบ `X-CSRF-Token` บน method ที่เปลี่ยน state

ไม่มี id_token ใน browser, ไม่มี GIS script, ไม่มี `Authorization` header.

> **สำคัญสุด:** `returnTo` ต้องเป็น path เดียวกับ origin (relative, ขึ้นต้น `/`) และอยู่ใน allowlist ฝั่ง server
> (`AdminSession:ReturnUrlAllowlist`). ค่านอก allowlist จะถูกแทนด้วย default path (กัน open-redirect).

## Session + cookie

| Cookie | อ่านจาก JS ได้ | อายุ/พฤติกรรม |
|---|---|---|
| `__Host-adm_session` (dev-http: `adm_session`) | **ไม่** (HttpOnly) | opaque 256-bit; server เก็บแค่ SHA-256 hash; idle 30m, absolute 8h |
| `adm_csrf` | ได้ (ไม่ HttpOnly) | คู่กับ session; ใช้ทำ double-submit (ดูล่าง) |

- **Rotation:** server หมุน session cookie ให้เองเป็นระยะ (ทุก ~15m) ผ่าน `Set-Cookie` ใน response ปกติ —
  FE ไม่ต้องทำอะไร (browser เปลี่ยน cookie ให้). token เก่าใช้ได้ต่ออีกชั่วครู่ (grace) ระหว่าง request ที่ค้าง
- **Revocation ทันที:** suspend admin / logout-all / ตรวจพบการ replay token เก่า -> ทั้ง family ถูก revoke,
  request ถัดไป 401 ทันที (ไม่ต้องรอ token หมดอายุ)
- session เป็น `SameSite=Lax` (same-site deploy) หรือ `None; Secure` (cross-site) — ตั้งฝั่ง server

## CSRF (double-submit) — บังคับบน POST/PUT/PATCH/DELETE

ทุก request ที่เปลี่ยน state ไปยัง `/admin/*` ต้องแนบ header `X-CSRF-Token` ที่ **ค่าตรงกับ cookie `adm_csrf`**
มิฉะนั้น **403**. GET/HEAD/OPTIONS ไม่ต้อง (login/callback ที่เป็น GET จึงผ่าน).

```js
const readCsrf = () =>
  document.cookie.split('; ').find(c => c.startsWith('adm_csrf='))?.split('=')[1] ?? '';

const api = (path, opts = {}) => fetch(`${API_BASE}${path}`, {
  ...opts,
  credentials: 'include',                         // ส่ง session cookie (BFF) — จำเป็น
  headers: {
    'Content-Type': 'application/json',
    'X-CSRF-Token': readCsrf(),                   // double-submit; server เทียบกับ cookie adm_csrf
    ...opts.headers,
  },
});
```

## Setup ฝั่ง FE

- **ไม่** ต้องขอ Google OAuth client เอง, **ไม่** ต้องโหลด GIS script. client id + secret เป็นของ server
  (confidential client, ฉีดผ่าน `Google__Oidc__ClientId` / `Google__Oidc__ClientSecret`)
- ปุ่ม "Sign in with Google" = ลิงก์/redirect ไป `${API_BASE}/admin/auth/login?returnTo=${encodeURIComponent(path)}`
  (top-level navigation — อย่าใช้ fetch)
- ทุก API call ตั้ง `credentials: 'include'` (ตรงข้ามกับโมเดลเดิม — ตอนนี้ auth = cookie)
- admin SPA origin ต้องอยู่ใน `Cors__AdminOrigins` ฝั่ง server (เปิด `AllowCredentials` ให้เฉพาะ origin นี้)

```js
// login
function login(returnTo = '/') {
  window.location.assign(`${API_BASE}/admin/auth/login?returnTo=${encodeURIComponent(returnTo)}`);
}
```

## ขั้นแรกหลัง login: `GET /admin/me`

หลัง callback set cookie + redirect กลับ `returnTo` แล้ว, FE ยิง `/admin/me` (พร้อม `credentials: 'include'`)
เพื่ออ่าน identity/scope. First-login binding (bind invited Scoped by email / self-provision Super จาก allowlist)
server จัดการตอน callback แล้ว — FE ไม่ต้องส่งอะไรพิเศษ.

```js
async function bootstrap() {
  const res = await api('/admin/me');
  if (res.status === 401) return login(location.pathname);  // ไม่มี session / หมด / ถูก revoke -> re-login
  if (res.status === 403) return showNotActive();           // resolved แต่ suspended / ไม่ active
  renderNav(await res.json());                              // ใช้ tier + accessibleTenants จัด UI
}
```

Response shape (ไม่เปลี่ยนจากเดิม):

```jsonc
// Super — เห็นทุก tenant
{ "adminId": "…", "email": "a@x.com", "tier": "Super", "accessibleTenants": { "isUnrestricted": true } }

// Scoped — เห็นเฉพาะ tenant ที่ถูก assign
{
  "adminId": "…", "email": "b@x.com", "tier": "Scoped",
  "accessibleTenants": { "isUnrestricted": false, "tenants": [ { "id": "…", "code": "acme" } ] }
}
```

`tier` มี 2 ค่า: `"Super"` | `"Scoped"`. ใช้ตัดสินใจซ่อน/โชว์ action ที่เป็น Super-only.

## Endpoints

auth = **session cookie** (`credentials: 'include'`). method ที่เปลี่ยน state ต้องมี `X-CSRF-Token`. Super-only =
Scoped ยิงโดน 403.

| Method | Path | Tier | CSRF | Body | Success | Note |
|---|---|---|---|---|---|---|
| GET | `/admin/auth/login` | — (anon) | — | — | 302 | redirect ไป Google; `?returnTo=<allowlisted path>` |
| POST | `/admin/auth/logout` | any | ต้อง | — | 204 | revoke session family ปัจจุบัน (อุปกรณ์นี้) + เคลียร์ cookie |
| POST | `/admin/auth/logout-all` | any | ต้อง | — | 204 | revoke ทุก session ของ admin นี้ (ทุกอุปกรณ์) |
| GET | `/admin/me` | any | — | — | 200 | bootstrap identity/scope |
| GET | `/admin/tenants/{code}` | any | — | — | 200 | scoped read; นอก scope/ไม่มี -> 404 |
| POST | `/admin/tenants` | **Super** | ต้อง | provision body | 201 | provision tenant (ดู reference 2.4); dup code -> 409 |
| POST | `/admin/admins` | **Super** | ต้อง | `{ "email": "…" }` | 201 | invite Scoped admin (bind ตอน login แรกของ invitee) |
| POST | `/admin/admins/{id}/tenants` | **Super** | ต้อง | `{ "tenantId": "…" }` | 200 | assign tenant; inactive/unknown/dup -> 409 |
| DELETE | `/admin/admins/{id}/tenants/{tenantId}` | **Super** | ต้อง | — | 204 | unassign; unknown -> 404 |
| POST | `/admin/admins/{id}/suspend` | **Super** | ต้อง | — | 204 | suspend; suspend ตัวเอง -> 403 |

`adminId` / `id` / `tenantId` เป็น Guid. JSON body/field เป็น camelCase.

## Logout

```js
async function logout(all = false) {
  await api(`/admin/auth/logout${all ? '-all' : ''}`, { method: 'POST' }); // CSRF + cookie แนบให้โดย api()
  login();                                                                  // กลับไปหน้า sign-in
}
```

## Error model

ทุก error เป็น RFC7807 ProblemDetails — `Content-Type: application/problem+json`, มี `title` + `status`.

| Status | ความหมาย | FE ทำอะไร |
|---|---|---|
| 401 | ไม่มี session cookie / session หมด/ถูก revoke / ตรวจพบ replay (reuse) | redirect ไป `/admin/auth/login` |
| 403 | session valid แต่: account suspended / ไม่ active / tier ไม่พอ / **CSRF token หาย/ไม่ตรง** | "ไม่มีสิทธิ์" หรือ refresh CSRF |
| 404 | tenant นอก scope หรือไม่มีจริง (กัน existence leak) | not-found |
| 409 | duplicate (code / assignment ซ้ำ) | conflict |
| 400 | body ผิด format | validation error |

> callback ที่ login ไม่ผ่าน (state ผิด / `email_verified=false` / hosted-domain ไม่ตรง / ไม่ allowlist /
> suspended) server redirect ไป `Google:Oidc:ErrorPath` พร้อม `?reason=<label>` (ไม่ใช่ JSON) — FE หน้า error
> อ่าน `reason` ได้.

## Dev / CORS

- API เดียว serve ทั้ง 2 SPA, **CORS แยก policy**: admin = credentialed (cookie XHR), tenant = no credentials.
  dev origin: admin = `http://localhost:5130` (`Cors__AdminOrigins`), tenant = `http://localhost:5120`
  (`Cors__AllowedOrigins`). prod ต้องตั้ง origin จริง — ไม่ตั้ง = block ทุก cross-origin
- admin XHR **ต้อง** `credentials: 'include'` ถึงจะส่ง cookie; tenant ห้าม (ยัง Bearer เหมือนเดิม)
- dev-http (localhost http): cookie ถอด `Secure` + ใช้ชื่อไม่มี `__Host-` prefix อัตโนมัติ — FE อ่าน `adm_csrf`
  ได้เหมือนกัน
- backend dev ต้องใส่ OIDC client id + secret จริงที่ `Google__Oidc__ClientId` / `Google__Oidc__ClientSecret`
  (user-secrets) ถึงจะ login จริงได้; placeholder boot ได้แต่ login ไม่ผ่าน
- bootstrap Super admin คนแรก: backend ใส่ Google `sub` ที่ `AdminAllowlist__Subjects__0`
- OpenAPI document เปิดเฉพาะ Development (`/openapi/...`) — prod ไม่ publish

## Source of truth

- OIDC login + callback (challenge/establish session): `src/Hosts/Api/AdminOidcAuthentication.cs`,
  `src/Hosts/Api/AdminLoginService.cs`
- session auth + rotation/reuse/revocation: `src/Hosts/Api/AdminSessionAuthenticationHandler.cs`,
  `src/Modules/Admin/Admin.Infrastructure/Persistence/AdminSessionStore.cs`
- cookies (session + CSRF): `src/Hosts/Api/AdminSessionCookies.cs`; CSRF filter: `src/Hosts/Api/AdminCsrfFilter.cs`
- routes (`/admin` route group): `src/Hosts/Api/Program.cs`
- tenant Bearer (unchanged): `src/BuildingBlocks/BuildingBlocks.Web/GoogleAuthenticationExtensions.cs`
- CORS split: `src/BuildingBlocks/BuildingBlocks.Web/CorsExtensions.cs`
- tier enum: `src/Modules/Admin/Admin.Domain/AdminTier.cs`
