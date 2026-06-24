# Admin Console (FE) — เชื่อม Backend SSO (OIDC BFF)

Admin auth = **server-side OIDC BFF**. FE **ไม่ถือ token**, **เลิกใช้ Google Identity (GIS) button**.
login = redirect ไป backend, session อยู่ใน httpOnly cookie ที่ backend จัดการ. เอกสารนี้คือสิ่งที่ฝั่ง FE
ต้องตั้งค่า. contract เต็ม (flow, rotation, security) ดู [admin-google-sso.md](./admin-google-sso.md).

**Ports (dev):** API `http://localhost:5100` · Admin SPA `http://localhost:5200`

## 1. Proxy — บังคับ (ต้อง same-origin)

backend redirect หลัง login = path บน origin เดียว และ cookie ผูกกับ origin → SPA กับ API ต้องเป็น origin
เดียวกัน. ตั้ง Next.js proxy:

```js
// next.config.js
module.exports = {
  async rewrites() {
    return [{ source: '/admin/:path*', destination: 'http://localhost:5100/admin/:path*' }]
  },
}
```

Next.js rewrites ส่ง `X-Forwarded-Host` ให้ backend เอง — backend honor แล้ว (`UseForwardedHeaders`) ไม่ต้องทำเพิ่ม.

## 2. Login = full-page navigate (ไม่ใช่ fetch)

```js
window.location.href = '/admin/auth/login?returnTo=' + encodeURIComponent('/dashboard')
```

fetch ไม่ได้ เพราะ flow เด้งออกไป Google. เสร็จแล้ว browser กลับมาที่ `returnTo`.

## 3. เช็คสถานะ login

session cookie = httpOnly → JS อ่านไม่ได้ (ตั้งใจ กัน XSS). เช็คด้วย `GET /admin/me`:
`200` = login แล้ว (ได้ข้อมูล admin) | `401` = ยัง → ส่งไป login.

## 4. เรียก admin API — แนบ cookie เสมอ

```js
fetch('/admin/me', { credentials: 'include' })
```

## 5. CSRF — เฉพาะ POST/PUT/PATCH/DELETE

อ่าน cookie `adm_csrf` (JS อ่านได้) → ส่ง header `X-CSRF-Token`:

```js
const csrf = decodeURIComponent(document.cookie.match(/adm_csrf=([^;]+)/)?.[1] ?? '')
fetch('/admin/tenants', {
  method: 'POST', credentials: 'include',
  headers: { 'X-CSRF-Token': csrf, 'Content-Type': 'application/json' },
  body: JSON.stringify(payload),
})
```

## 6. Logout

```js
POST /admin/auth/logout       // device นี้
POST /admin/auth/logout-all   // ทุก device
```

ทั้งคู่ต้องแนบ `X-CSRF-Token` + `credentials: 'include'`.

## Endpoints

| method | path | ใช้ |
|---|---|---|
| GET | `/admin/auth/login?returnTo=` | เริ่ม SSO (navigate) |
| GET | `/admin/me` | admin ปัจจุบัน / เช็ค login |
| POST | `/admin/auth/logout` | ออก (device นี้) + CSRF |
| POST | `/admin/auth/logout-all` | ออกทุก device + CSRF |
| POST/PUT/DELETE | `/admin/tenants`, ... | admin APIs (ต้อง CSRF) |

## returnTo allowlist

หลัง login backend redirect ไปได้เฉพาะ path ที่อยู่ใน `AdminSession:ReturnUrlAllowlist` (กัน open-redirect);
path นอก list — และ absolute URL — ถูก fallback เป็น `AdminSession:DefaultReturnPath`.

**committed default = `["/"]` เท่านั้น** (conservative). route ปลายทางจริงของ FE ตั้งต่อ deployment:
- dev (`appsettings.Development.json`): `/`, `/main`, `/dashboard`, `/tenants`
- staging/prod: env `AdminSession__ReturnUrlAllowlist__0=/`, `__1=/dashboard`, ... (ดู deploy runbook)

**สำคัญ:** helper ด้านล่าง default `returnTo='/dashboard'` → deployment นั้นต้องมี `/dashboard` ใน allowlist
ไม่งั้นถูกเด้งกลับ `/` (`DefaultReturnPath`). ขอ ops เพิ่ม route ที่ FE ใช้จริง.

## ห้าม

- เลิกใช้ GIS SDK / id-token / `Authorization: Bearer` (ของเก่า)
- อย่าอ่าน/เก็บ session cookie เอง (httpOnly)
- อย่าเรียก API ข้าม origin ตรง — ต้องผ่าน proxy (ข้อ 1)

## helper รวม

```js
// lib/adminApi.js
const cookie = (n) =>
  decodeURIComponent(document.cookie.match(new RegExp('(?:^|; )' + n + '=([^;]+)'))?.[1] ?? '')

export function login(returnTo = '/dashboard') {
  window.location.href = '/admin/auth/login?returnTo=' + encodeURIComponent(returnTo)
}

export async function adminFetch(path, opts = {}) {
  const method = (opts.method ?? 'GET').toUpperCase()
  const headers = { ...opts.headers }
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) headers['X-CSRF-Token'] = cookie('adm_csrf')
  const res = await fetch(path, { ...opts, headers, credentials: 'include' })
  if (res.status === 401) login(location.pathname) // session หมด -> re-login
  return res
}

export const logout = () => adminFetch('/admin/auth/logout', { method: 'POST' })
```

## backend ทำให้แล้ว (FE ไม่ต้องแตะ)

- CORS allow `http://localhost:5200`
- honor `X-Forwarded-Host` → `redirect_uri` ออกมาเป็น origin ของ FE
- Google redirect URI registration (ฝั่ง ops/backend)

## prod

topology เดียวกัน (reverse proxy → same-origin), cookie เป็น `Secure` + `__Host-` อัตโนมัติบน https.
FE code ไม่ต้องเปลี่ยน (ยัง `credentials: 'include'` + อ่าน `adm_csrf` เหมือนเดิม).
