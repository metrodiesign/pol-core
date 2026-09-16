# Agent OAuth และ Microsoft CIAM end-session

คู่มือนี้กำหนด backend contract ที่ `pol-merchant` ใช้เลือก Microsoft account ตอน Register และจบ CIAM session โดยไม่ hardcode tenant URL

## API contract

| Method | Path | ผู้เรียก | ผลสำเร็จ | Failure สำคัญ |
|---|---|---|---|---|
| `GET` | `/oauth/authorize` | Browser | `302` ไป Microsoft CIAM | Agent prompt ที่ไม่อนุญาตได้ `400` OAuth `invalid_request` |
| `POST` | `/api/v1/auth/logout` | Agent Bearer | `204` หลัง revoke OpenIddict authorization | ไม่มีหรือใช้ token เดิมได้ `401` |
| `GET` | `/api/v1/auth/agents/logout` | Browser navigation | `302` ไป CIAM `end_session_endpoint` | metadata ไม่มี endpoint ได้ `503` พร้อม `code=end_session_not_configured` |

### เลือก account ตอน Register

`pol-merchant` ส่ง `prompt=select_account` เฉพาะ Register request ที่เรียก `/oauth/authorize` ด้วย `client_id=pol-merchant` ระบบจะ forward ค่านี้ไป CIAM

Login ปกติไม่ส่ง `prompt` ระบบจะไม่เพิ่มค่าให้ จึงยังใช้ SSO session เดิม ค่า prompt อื่นของ Agent ถูกปฏิเสธแบบ fail closed

### Logout สองชั้น

1. เรียก `POST /api/v1/auth/logout` ด้วย Agent access token เพื่อ revoke authorization ของ platform
2. เมื่อได้ `204` ให้ล้าง token ฝั่ง browser แล้ว navigate ไป `GET /api/v1/auth/agents/logout`
3. Backend อ่าน `end_session_endpoint` จาก Agent OIDC configuration และ redirect browser ไป endpoint นั้น
4. Backend กำหนด `post_logout_redirect_uri` เป็น `<IdentityAccess:AgentWebAppBaseUrl>/login`

Browser endpoint ไม่รับ `returnTo`, post-logout URI หรือ tenant URL จาก client Query parameter ที่ส่งมาไม่สามารถ override destination ที่ server สร้าง

`GET /.well-known/oauth-authorization-server` ประกาศ backend browser endpoint นี้เป็น `end_session_endpoint` เพื่อให้ consumer derive จาก API metadata ได้โดยไม่ hardcode CIAM authority

URL ที่ backend สร้างไม่มี `id_token_hint`, access token, refresh token, authorization code, PKCE verifier, nonce, state หรือ registration session

## Backend configuration

ตั้งค่าต่อ environment ผ่าน configuration provider หรือ secret store ที่ใช้อยู่:

| Key | ความหมาย |
|---|---|
| `IdentityAccess:Agent:Authority` | tenant-pinned CIAM authority ที่ใช้โหลด OIDC metadata |
| `IdentityAccess:Agent:ClientId` | confidential Agent OIDC client ของ API |
| `IdentityAccess:Agent:ClientSecret` | secret จาก secret store ห้าม commit |
| `IdentityAccess:AgentWebAppBaseUrl` | origin ของ `pol-merchant` เช่น `https://localhost:3002` |
| `IdentityAccess:AgentClientId` | OpenIddict public client ID ค่า canonical คือ `pol-merchant` |

เมื่อเปิด Agent provider ค่า `IdentityAccess:AgentWebAppBaseUrl` ต้องเป็น HTTP(S) origin แบบ absolute และห้ามมี path, query, fragment หรือ user info ระบบ fail startup เมื่อค่าไม่ผ่าน validation

CIAM discovery metadata ต้องประกาศ absolute HTTPS `end_session_endpoint` หากไม่มีหรืออ่าน metadata ไม่ได้ endpoint logout จะ fail closed โดยไม่ redirect

## Entra app registration

เพิ่ม Web post-logout redirect URI แบบ exact สำหรับ local development:

```text
https://localhost:3002/login
```

สำหรับ staging และ production ให้สร้าง URI จากค่า `IdentityAccess:AgentWebAppBaseUrl` ของ environment จริงแล้วต่อ fixed path `/login` ห้ามคาดเดา hostname และห้ามใช้ wildcard

การแก้ Entra app registration เป็น external operation ที่ operator ต้องได้รับ human authorization ก่อน งาน source code นี้ไม่แก้ app registration หรือ production configuration

## Browser verification

1. เปิด Login แล้วตรวจ downstream CIAM authorization URL ไม่มี `prompt`
2. เปิด Register แล้วตรวจ downstream CIAM authorization URL มี `prompt=select_account` และแสดง account chooser
3. Login ให้สำเร็จ เรียก `POST /api/v1/auth/logout` แล้วตรวจว่า access token เดิมเรียก `/api/v1/me` ได้ `401`
4. Navigate ไป `/api/v1/auth/agents/logout` แล้วตรวจว่า host/path มาจาก CIAM metadata และ query มี post-logout URI ของ environment ตามด้วย `/login`
5. ยืนยันว่า Microsoft CIAM session จบ และ browser กลับหน้า `/login` โดย Login ครั้งถัดไปไม่ reuse account เดิมแบบเงียบ

ข้อ 4-5 จะผ่านได้ต่อเมื่อ operator เพิ่ม post-logout redirect URI ใน Entra app registration แล้ว
