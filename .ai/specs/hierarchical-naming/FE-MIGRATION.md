# FE Migration Notes — hierarchical-naming

> สำหรับทีม FE ทั้ง 2 repo (`pol-tenant`/merchant-user console, `pol-admin`) ที่เรียก backend `pol-core` นี้อยู่.
> `hierarchical-naming` เป็น breaking change ฝั่ง client แบบ big-bang เหมือน `rf1-schema-reset` ก่อนหน้า —
> ไม่มี alias/route เก่าเหลือ, ไม่มี dual-write ช่วงเปลี่ยนผ่าน. เรียก path เก่าจะได้ 404 ทันทีหลัง deploy.
> ถ้าตามลิงก์มาจาก [`rf1-schema-reset/FE-MIGRATION.md`](../rf1-schema-reset/FE-MIGRATION.md) — เอกสารนั้น
> คุม breaking change รอบ rf1 (auth model, Money wire format, JSON field rename); เอกสารนี้คุมเฉพาะรอบสอง
> (route/permission-key/OpenAPI-id rename) เท่านั้น, ไม่ทับซ้อนกัน. Satisfies REQ-12 (all criteria).

## 1. Route rename (เก่า → ใหม่) — REQ-12.1

ไม่มี alias/redirect ของ path เก่าเหลือเลย — เรียก path เก่าจะได้ 404 ทันทีหลัง deploy. Auth mechanism
(scheme, cookie, CSRF) **ไม่เปลี่ยน** บนทุกแถวข้างล่าง — เปลี่ยนแค่ path.

| เก่า | ใหม่ | หมายเหตุ |
|---|---|---|
| `/api/v1/merchant-users/**` (ทั้ง surface: `auth/login`, `auth/callback`, `auth/logout`, `auth/logout-all`, `register`, `me`, `permissions`, `roles`, `roles/{code}`, `roles/{id}/roles` set) | `/api/v1/merchants/users/**` | ย้ายยกก้อน — เปลี่ยนแค่ prefix ตรงกลาง (`merchant-users` → `merchants/users`), path ที่เหลือหลัง prefix เดิมเป๊ะ |
| `POST /api/v1/admins/merchants` | `POST /api/v1/merchants` | ย้ายออกจาก `/admins` group มาแมพตรงบน `/api/v1`; คุม 4 อย่างเดิม (CSRF filter, admin CORS, `admin` policy, Super tier) ติดมาด้วยครบ — ไม่มี auth เปลี่ยน |
| `GET /api/v1/admins/merchants/{code}` | `GET /api/v1/merchants/{code}` | เหมือนกัน. `{code}` **ไม่มี route constraint** ทั้งเก่าและใหม่ (ตั้งใจ — ดู design.md §4) |
| `POST /api/v1/admins/merchant-users/{subject}/approve` | `POST /api/v1/admins/merchants/users/{subject}/approve` | ยังอยู่ใต้ `/admins` เหมือนเดิม (เป็น cross-plane action ของฝั่ง admin, ไม่ใช่ merchant-user self-service) |
| `POST /api/v1/admins/merchant-users/{subject}/reject` | `POST /api/v1/admins/merchants/users/{subject}/reject` | เหมือนกัน |
| `/api/v1/admins/master-data/{positions\|offices\|levels\|divisions}` (list/create/update ทั้ง 4 list) | `/api/v1/admins/{positions\|offices\|levels\|divisions}` | segment `master-data` ถูก **drop ไม่ใช่ rename** — 4 list ยังอยู่ใต้ `/admins` โดยตรง แค่ตัด wrapper ตรงกลางออก |

ที่ **ไม่เปลี่ยน** (ยังเป็น path เดิมเป๊ะ ๆ): `/products`, `/carts`, `/checkouts`, `/orders`, `/payments`,
`/reports`, `/webhooks`, `/api/v1/admins/auth/*`, `/api/v1/admins` (CRUD ตัว admin เอง),
`/api/v1/admins/roles/*`, `/api/v1/admins/{id}/profile`, `/api/v1/admins/{id}/sessions`,
`/api/v1/admins/{id}/merchants` (assign/unassign) ฯลฯ — เฉพาะ segment ที่เกี่ยวกับ merchant-user
self-service, merchant provisioning, หรือ master-data wrapper เท่านั้นที่เปลี่ยน.

## 2. Location response header changes — REQ-12.2

FE ที่อ่าน header `Location` จาก `201 Created` เพื่อ navigate/refetch ต้องอัปเดต 4 จุดนี้ (grep-verified
จาก `src/Hosts/Api/Program.cs`, `git show` ของ task 8's commit):

| endpoint (operation id) | เก่า | ใหม่ |
|---|---|---|
| `POST /api/v1/merchants` (`ProvisionMerchant`) | `/api/v1/admins/merchants/{code}` | `/api/v1/merchants/{code}` |
| `POST /api/v1/merchants/users/register` (`MerchantUserRegister`) | `/api/v1/merchant-users/{merchantUserId}` | `/api/v1/merchants/users/{merchantUserId}` |
| `POST /api/v1/merchants/users/roles` (`CreateMerchantUserRole`) | `/api/v1/merchant-users/roles/{code}` | `/api/v1/merchants/users/roles/{code}` |
| master-data create (`Create{segment}`, `segment` ∈ `positions`/`offices`/`levels`/`divisions`) | `/api/v1/admins/master-data/{segment}/{id}` | `/api/v1/admins/{segment}/{id}` |

**ไม่เปลี่ยน** (audit ไว้เพื่อไม่ให้ FE เผลอแก้ผิด): `POST /api/v1/admins` (`CreateScopedAdmin`) ยัง Location
`/api/v1/admins/{adminId}` เหมือนเดิม; `POST /api/v1/admins/roles` (`CreateRole`) ยัง Location
`/api/v1/admins/roles/{code}` เหมือนเดิม — ทั้งคู่ไม่ได้อยู่ในเส้นทางที่ spec นี้ย้าย.

## 3. Permission-key string changes — REQ-12.3

Key ที่ FE gate UI (ปุ่ม/เมนู/route guard) อยู่บน ต้องอัปเดต:

| catalog | เก่า | ใหม่ |
|---|---|---|
| admin | `merchant_user.approve` | `merchants.users.approve` |
| admin | `merchant_user.reject` | `merchants.users.reject` |
| admin | group key `merchant_user` | group key `merchants.users` |
| merchant-user | `merchant_user.roles.view` | `roles.view` |
| merchant-user | `merchant_user.roles.manage` | `roles.manage` |
| merchant-user | `merchant_user.user.roles` | `users.roles` |

**ไม่เปลี่ยน** — อย่า migrate เกิน (ทั้งสอง catalog มี key อื่นที่ไม่แตะเลย):
- admin catalog: `txn.*`, `merchant.*` (view/manage ตัว merchant เอง — คนละอันกับ `merchants.users.*`
  ข้างบนที่เป็นเรื่อง approve/reject merchant-user), `invoice.*`, `settlement.*`, `user.*`, `audit.*`,
  `settings.*`, `apikey.*`
- merchant-user catalog: `product.*`, `payment.*`

## 4. OpenAPI security-scheme id — REQ-12.4

Generated client (ถ้า FE gen จาก OpenAPI/Scalar document) key scheme บนชื่อนี้ — เปลี่ยน:

| เก่า | ใหม่ |
|---|---|
| `PlatformUserSession` | `AdminSession` |

`MerchantUserSession` **ไม่เปลี่ยน** — คงชื่อเดิม (ไม่ใช่ typo, ตั้งใจ: ผู้ใช้ตัวนี้เป็น "user ของ merchant"
จริง ๆ ไม่ใช่ session ของ merchant องค์กร — เปลี่ยนชื่อจะสื่อผิด).

## 5. OpenAPI component schema id: `PspCode` → `Code` — REQ-12.5 (task 6 fallout)

ผลข้างเคียงจากการ rename type ใน task 6 (ไม่ได้ตั้งใจ rename wire contract แต่ generator key schema บนชื่อ
CLR type) — generated client's type name เปลี่ยน:

| เก่า | ใหม่ |
|---|---|
| component schema `PspCode` | component schema `Code` |

ค่า/รูปแบบ wire ของ field นี้ (string code เดิม) **ไม่เปลี่ยน** — เปลี่ยนแค่ชื่อ schema ใน
`components.schemas` ของ OpenAPI document ที่ codegen ใช้ตั้งชื่อ type.

## 6. Master-data operation ids — REQ-12.5

**ไม่เปลี่ยน.** Operation id ของทั้ง 12 endpoint (`List{segment}`/`Create{segment}`/`Update{segment}` ×
`positions`/`offices`/`levels`/`divisions`) มาจาก string literal `segment` ที่ผูกกับ endpoint ไม่ใช่จาก
route path — ตอนย้าย path (ตัด `master-data/` wrapper ออก, §1) โค้ดแตะแค่ `Results.Created(...)` กับ
parent route group เท่านั้น, บรรทัด `.WithName(...)` ไม่ถูกแก้เลย (verified: `git show` ของ task 8's commit
ไม่มี diff บนบรรทัด `WithName` ในบล็อก master-data). ค่าจริง (case-sensitive, มาจาก `segment` ตัวพิมพ์เล็ก
ที่ประกาศไว้ที่ `MapMasterCrud<T>(admin, "positions", ...)` เป็นต้น) คือ:

`Listpositions`, `Createpositions`, `Updatepositions`, `Listoffices`, `Createoffices`, `Updateoffices`,
`Listlevels`, `Createlevels`, `Updatelevels`, `Listdivisions`, `Createdivisions`, `Updatedivisions`

ถ้า generated client เคยอ้าง operation id พวกนี้อยู่แล้ว ไม่ต้องแก้อะไร — เฉพาะ URL path ที่ generator เรียก
ใต้ฝากระโปรงเท่านั้นที่เปลี่ยน (ตาม §1), ชื่อ method/function ที่ codegen สร้างให้ FE เรียกเหมือนเดิม.

## 7. OIDC callback path move — operator prerequisite

| เก่า | ใหม่ |
|---|---|
| `/api/v1/merchant-users/auth/callback` | `/api/v1/merchants/users/auth/callback` |

**นี่คือ contract ที่อยู่นอก repo ด้วย** — ต้องอัปเดต **Google Cloud Console → OAuth 2.0 Client ID
(merchant-user) → Authorized redirect URIs** ให้เป็น path ใหม่ **ก่อน** deploy branch นี้ ในทุก
environment (dev/staging/prod แยกกัน). ถ้าลืม: merchant-user Google login พังทันทีหลัง deploy (backend
ส่ง `redirect_uri` ใหม่ที่ Google ยังไม่รู้จัก → `Error 400: redirect_uri_mismatch`) แม้ CI จะเขียวอยู่ก็ตาม
(CI ไม่ทดสอบ Google Console). ทีม FE/backend ต้อง coordinate cutover ล่วงหน้า — ไม่ใช่ auto-fix ฝั่งใดฝั่งหนึ่ง.

## 8. ไม่เปลี่ยน — อย่า migrate เกิน

- Auth scheme id `MerchantUserSession` (§4)
- ชื่อ cookie ทั้งหมด (`__Host-adm_session`/`adm_csrf`, `__Host-mch_session`/`mch_csrf` และ dev-http
  variant ที่ไม่มี prefix) — ไม่แตะ
- Rate-limit policy names (`admin-auth`, `merchant-user-auth`, `psp-webhook`)
- Config section key ทุกตัว (`Google:Oidc`, `MerchantUser:Oidc`, `MerchantUser:Session`,
  `MerchantUser:Registration`, ฯลฯ) — ไม่กระทบ FE โดยตรง แต่ถ้า FE console เข้าไปอ่าน config เหล่านี้ ก็ยัง
  key เดิม
- Event/outbox `MerchantUserRegistrationSubmitted` และ registry key ที่เกี่ยวข้อง — ไม่เปลี่ยน (out of
  scope ตามการออกแบบ — namespace `Contracts` เป็น flat vocabulary)
- **Request/response body shape ทุก endpoint ไม่เปลี่ยน** — field count, nesting, JSON key ทั้งหมดเดิม
  100%. นี่เป็นการ rename เส้นทาง/ชื่อ ไม่ใช่การเปลี่ยน payload

## Reference

- Route/permission/OpenAPI rename เต็ม: [design.md](design.md) §4 (Routes), §7 (Permission keys), §9
  (FE-facing contract changes)
- Requirements: [requirements.md](requirements.md) REQ-6 (routes), REQ-11 (wire strings), REQ-12 (this doc)
- Task evidence (บรรทัด/ค่าที่ verify จริงตอน implement): [tasks.md](tasks.md) task 6 (schema id
  `PspCode`→`Code`), task 8 (routes + Location headers), task 10 (permission keys + auth scheme + callback)
- รอบก่อนหน้า (auth model, Money wire format, JSON field rename — คนละก้อนกับเอกสารนี้):
  [`rf1-schema-reset/FE-MIGRATION.md`](../rf1-schema-reset/FE-MIGRATION.md)
- Operator-side runbook (ไม่กระทบ FE โดยตรง แต่กระทบว่า backend คนไหน boot ได้/login ได้):
  [`docs/runbooks/local-dev-run.md`](../../../docs/runbooks/local-dev-run.md) §5
