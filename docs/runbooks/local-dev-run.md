# คู่มือการรัน pol-core (Local Dev)

คู่มือรันสำหรับทีมพัฒนา ครอบคลุม first-time setup, รันประจำวัน (DB / API),
การตั้งค่า Google SSO (Admin + Merchant-user), การรันเทส, และ troubleshooting ของปัญหาที่เจอจริง.

ทุก path อ้างจาก repo root `pol-core/`. ค่า secret อ่านจาก `.env` (gitignored) เสมอ — ไม่มี
ค่าจริงในเอกสารนี้.

---

## 1. Prerequisites

| tool | version | หมายเหตุ |
|---|---|---|
| .NET SDK | 10.x | `dotnet --version` ต้องขึ้น 10.x |
| Docker + Docker Compose | ล่าสุด | รัน SQL Server 2025 container |
| SQL Server 2025 | ผ่าน container | ไม่ต้องติดตั้งเอง |

EF tooling:

```
dotnet tool install --global dotnet-ef
```

---

## 2. First-time setup (ทำครั้งเดียว)

### 2.1 env + git hooks

```
cp .env.example .env
```

แก้ `.env` ใส่ค่า LOCAL (ห้ามใส่ secret จริงของ prod):
- `MSSQL_SA_PASSWORD` — รหัส `sa` ของ container (compose ส่งให้ทั้ง `pol-db` และ `pol-db-init`)
- `POL_APP_PASSWORD` — **principal เดียวของ runtime** (`pol_app`). strong, **ห้ามมีชื่อ login อยู่ในรหัส**
  (SQL Server password policy)
- `ConnectionStrings__*` — ใส่รหัสให้ตรงกับ `POL_APP_PASSWORD` (ทุก connection string ใช้ `pol_app` ตัวเดียวกัน)
- `POL_DESIGN_SQL` — ใช้ `sa` (migration ต้องมีสิทธิ์ DDL ที่ app principal ไม่มี)

> มีรหัส DB แค่ **2 ตัว** (`MSSQL_SA_PASSWORD` + `POL_APP_PASSWORD`). `POL_ADMIN_PASSWORD` /
> `POL_WORKER_PASSWORD` **ไม่มีอยู่แล้ว** — rls-to-query-filter task 8 (RLS teardown) ยุบ
> `pol_admin`/`pol_worker`/`pol_resolver`/`pol_vault_auditor` เข้า `pol_app` ตัวเดียว. ยืนยันได้จาก
> `docker-compose.yml` (`pol-db-init.environment` ส่งแค่ 2 ตัวนี้) และ `.github/workflows/ci.yml`.
- `Vault__MasterKeyBase64` — gen ของจริง local: `head -c 32 /dev/urandom | base64`

เปิด git hooks (enforcement floor):

```
git config core.hooksPath .githooks
```

### 2.2 ยก DB + สร้าง principal

```
docker compose up -d
docker compose ps -a
```

ยก 3 service:
- `pol-db` — SQL Server 2025 ที่ `localhost:11433`
- `pol-db-init` — รัน `docker/bootstrap/01-principals.sql` (idempotent): สร้าง DB `VCentralPay` + login/user
  **`pol_app` ตัวเดียว**. exit 0 เมื่อเสร็จ — ยืนยันด้วย `docker compose ps -a` เอง เพราะ `up -d` ไม่ฟ้อง
  (คืน exit 0 เสมอแม้ container นี้ exit ไม่ใช่ 0). เห็น `Exited (1)` มักเป็น collation gate ยิง (DB เดิม
  collation ไม่ตรง `Thai_100_CI_AS`) — ยืนยันสาเหตุจริงด้วย `docker compose logs pol-db-init` แล้วแก้ด้วย
  `docker compose down -v && docker compose up -d` (อย่า drop เฉพาะ `VCentralPay` ตามข้อความ THROW ตรงตัว
  เพราะ sim DB `hippodb`/`mammothdb` ต้อง recreate ใหม่ด้วย)
- `seq` (container `pol-seq`) — Seq sink สำหรับ security/denial telemetry (rls-to-query-filter task 9,
  REQ-13.4). UI ที่ `http://localhost:5341` (bind `127.0.0.1` เท่านั้น, local dev เปิดแบบไม่มี auth ผ่าน
  `SEQ_FIRSTRUN_NOAUTHENTICATION`). host POST event ไปที่ `http://seq:5341/api/events/raw`

> bootstrap สร้าง **principal เดียว**. `pol_admin` / `pol_worker` / `pol_rls_bypass` **ไม่มีแล้ว** — header
> comment ในไฟล์ `01-principals.sql` อธิบายไว้เอง (RLS teardown ยุบทุก principal + bypass role เข้า `pol_app`
> เพราะ app-layer EF query-filter มาแทน SQL Server RLS).

> object-level GRANT/DENY ไม่ได้อยู่ในไฟล์นี้ — มันอยู่ใน EF migration (หลังตารางถูกสร้าง).

### 2.3 รัน migrations

Dev: API auto-migrate ตอน boot ถ้า `ConnectionStrings:Migrator` ถูกตั้ง (ดู §4). หรือรันเองตรง:

```
POL_DESIGN_SQL="<sa conn string>" \
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api
```

> **Cutover สำคัญ (PR #68 — schema `producer` -> `VCentralPay`):** การ rename ทำโดย **เขียน migration
> history เดิมทับ** (ไม่ใช่เพิ่ม transfer migration) — เป็น big-bang reset-only แบบเดียวกับ api-route-scheme.
> DB ที่ **apply migration IDs พวกนี้ไปแล้วภายใต้ schema `producer`** (เช่น dev DB `:11433` ที่รันมาก่อน #68)
> จะ **ไม่ re-run** body ที่แก้ เพราะ ID ถูกบันทึกใน `__EFMigrationsHistory` แล้ว -> โค้ดชี้ `VCentralPay.*`
> แต่ของจริงยังอยู่ `producer.*` -> query/proc แรกพัง. ต้อง **recreate DB สด ครั้งเดียว** หลัง pull:
>
> ```
> docker compose down -v && docker compose up -d   # ล้าง volume -> bootstrap DB ใหม่
> # แล้ว migrate (§2.3 หรือ auto-migrate ตอน API boot) -> schema VCentralPay
> ```
>
> Fresh clone / CI ไม่กระทบ (สร้างจากศูนย์อยู่แล้ว).

> **Cutover สำคัญ (rename DB catalog `PaymentOrchestration` -> `VCentralPay`):** ชื่อ database เปลี่ยน
> (ไม่ใช่ schema) — bootstrap (`01-principals.sql`) สร้าง DB ตามค่า `DbName`/`DB_NAME`/`POL_DB` ใหม่
> `VCentralPay`. volume `pol-db-data` เดิมยังมี DB เก่า `PaymentOrchestration` ค้าง ทำให้ปนกัน. reset-only
> ครั้งเดียวหลัง pull:
>
> ```
> # 1. อัปเดตไฟล์ local (gitignored): .env + .env.integration + appsettings.Development.json -> VCentralPay
> # 2. ล้าง volume + bootstrap DB VCentralPay ใหม่
> docker compose down -v && docker compose up -d
> # 3. migrate (§2.3 หรือ auto-migrate ตอน API boot) -> schema VCentralPay ใน DB VCentralPay
> ```
>
> Integration DB `:11434` ก็อัปเดต `.env.integration` แล้ว recreate เป็น `VCentralPay` เช่นกัน. Fresh clone /
> CI ไม่กระทบ (สร้างจากศูนย์ด้วยชื่อใหม่). **มีข้อมูล prod จริง** (อนาคต): ใช้ `ALTER DATABASE [PaymentOrchestration]
> MODIFY NAME = [VCentralPay]` + backup ก่อน แทน down -v (bootstrap จะสร้าง VCentralPay ว่าง ทิ้งข้อมูลเดิม).

> **Cutover สำคัญ (rf1-schema-reset — schema เดียว `producer` -> multi-schema `shop`/`txn`/`admin`/`merch`/`sec`/`dbo`,
> actor rename ทั้งระบบ, Money -> decimal):** big-bang reset-only เหมือนเดิม (เขียน migration history ทับ ไม่ใช่ transfer
> migration) — ต้อง **recreate DB สดครั้งเดียว** หลัง pull บน DB ใดก็ตามที่เคย migrate มาก่อน 2026-07-12:
>
> ```
> docker compose down -v && docker compose up -d   # ล้าง volume -> bootstrap DB ใหม่ (catalog ยังชื่อ VCentralPay เดิม)
> # แล้ว migrate (§2.3 หรือ auto-migrate ตอน API boot) -> 3 migration ใหม่: InitialSchema/SecurityObjects/SeedData
> ```
>
> Operator ต้องแก้มือไฟล์ gitignored ต่อไปนี้เอง (ไม่มีใน PR — pattern เดียวกับ cutover ก่อนหน้า):
> - `.env` / `appsettings.Development.json`: คีย์ `ConnectionStrings__Producer` -> `ConnectionStrings__App` (จับคู่
>   principal `pol_app` เดิม; `POL_DESIGN_SQL` ไม่เปลี่ยน — คีย์ `Admin`/`Worker` ถูกถอดทิ้งภายหลังโดย RLS
>   teardown, ดู §3); `Tenant:DevTenantId` -> `Merchant:DevMerchantId`;
>   section OIDC `Producer:Oidc` -> `MerchantUser:Oidc` (env override `Producer__Oidc__ClientSecret` ->
>   `MerchantUser__Oidc__ClientSecret`)
> - `.env.integration`: **เดิมชี้ container แยกที่ `:11434`** (จัดการเองนอก `docker-compose.yml`, ไม่เคย reproducible
>   จริง) — **ไม่ใช่ pattern ที่ถูกต้องอีกต่อไป**: ชี้ `pol-db`/`:11433` ตัวเดียวกับ dev เสมอ (ดู §6 ที่แก้ใหม่ทั้งหมด)
> - DbContext ชื่อ `ProducerDbContext` -> `PolDbContext` ทุกจุดที่อ้างตรง (คำสั่ง `dotnet ef`, custom script ส่วนตัว)
>
> ตารางเดิม `ProducerAccounts`/`ProducerTenantAssignments`/`ProducerSessions`/`ProducerAuthAudits`/`ProducerRoles*` ->
> `MerchantUsers`/(ดูดซับเป็นคอลัมน์ `MerchantId` — ตารางแยกถูก drop)/`MerchantUserSessions`/`MerchantAuthAudits`/
> `MerchantUserRole*` (ทั้งหมดอยู่ schema `merch` ตอนนี้); `AdminAccounts`/`AdminTenantAssignments`/`AdminSessions`/
> `AdminAuthAudits` -> `PlatformUsers`/`PlatformMerchantAccess`/`PlatformUserSessions`/`PlatformAuthAudits` (schema
> `admin`). Fresh clone / CI ไม่กระทบ (สร้างจากศูนย์ด้วยชื่อใหม่อยู่แล้ว). รายละเอียด schema map เต็ม + rename map:
> [`.ai/specs/rf1-schema-reset/design.md`](../../.ai/specs/rf1-schema-reset/design.md).

---

## 3. Topology (ports / principals / connection strings)

| host | port | principal | ใช้ทำอะไร |
|---|---|---|---|
| SQL Server (dev + integration test) | `11433` | — | DB หลัก `VCentralPay` — **container เดียวกันทั้ง dev และ Integration suite** (rf1; ไม่มี container แยกอีกแล้ว, ดู §6) |
| Seq (container `pol-seq`) | `5341` (loopback) | — | security/denial telemetry sink + UI |
| API (`src/Hosts/Api`) | `5100` (http) / `5101` (https) | `pol_app` | REST + BFF auth + background dispatch (in-process) |
| FE admin console (repo แยก) | `5200` | — | Next.js, proxy `/api/v1/admins/*` ไป `:5100` — **origin ที่ browser ใช้ login admin** |
| FE merchant-user console (repo แยก) | `5300` | — | Next.js, proxy `/api/v1/merchants/*` ไป `:5100` — **origin ที่ browser ใช้ login merchant-user** (`RegisterUrl` ชี้ `http://localhost:5300/register`) |

Connection strings (ASP.NET map `ConnectionStrings__<Name>` -> `ConnectionStrings:<Name>`):

| Name | login | สิทธิ์ |
|---|---|---|
| `App` | `pol_app` | **connection string เดียวของ runtime** — ทุก plane (merchant data + admin/merchant-user control-plane + background dispatch) ใช้ตัวนี้ |
| `Migrator` | `sa` | DDL — auto-migrate ตอน boot (Dev เท่านั้น) |

> หลักการ principal: **1 principal**. `src/Hosts/Api/appsettings.json` มี `ConnectionStrings` แค่คีย์ `App`
> (`User Id=pol_app`) — ไม่มี `Admin` / `Worker` แล้ว. การแยก merchant/admin ไม่ได้อยู่ที่ SQL principal
> หรือ RLS predicate อีกต่อไป แต่อยู่ที่ **EF global query filter + write authorizer ในชั้น app**
> (`sec.fn_merchant_predicate` ถูก drop ไปพร้อม RLS teardown). ดู `.ai/shared/ARCHITECTURE.md`.

---

## 4. รันประจำวัน

### 4.1 DB + Seq (ถ้ายังไม่ขึ้น)

```
docker compose up -d
```

ยกทั้ง `pol-db` (`:11433`) และ `pol-seq`. เปิด Seq UI ดู security/denial event ที่
`http://localhost:5341` (ไม่ต้อง login — local dev ปิด auth ไว้).

### 4.2 API

```
dotnet watch --project src/Hosts/Api/Api.csproj run
```

รอจนเห็น `Now listening on: http://localhost:5100`. Dev จะ auto-migrate ก่อน (log:
`Applied pending EF migrations`). hot reload จับการแก้ code อัตโนมัติ — **แต่การแก้ config
(`appsettings.*.json`) หรือ DI ต้อง restart เต็ม**.

> เปิดค้างใน terminal แยกของคุณเอง (หรือ tab IDE). อย่ารันผ่าน agent background — มันถูก
> kill ตอนจบ turn.

### 4.3 Outbox / background dispatch

**ไม่มี Worker host แยกให้รันแล้ว** — `src/Hosts/Worker/` ไม่มี `.csproj` เหลืออยู่ (เหลือแค่ `bin/`/`obj/`
litter จากการ build เก่า, ลบทิ้งได้). background dispatch รัน **in-process ใน `Api`** ผ่าน
`src/Hosts/Api/BackgroundDispatch/` (`BackgroundDispatchScope` / `WorkerActorContext` /
`WorkerWriteAuthorizer` — scope-discriminated ใช้ connection `App` ตัวเดียวกัน). รัน §4.2 ก็ได้ทั้ง REST
และ dispatcher.

### 4.4 FE console (repo แยก)

รันตาม README ของแต่ละ repo, ตั้ง API origin เป็น `http://localhost:5100`:

| console | port | proxy |
|---|---|---|
| admin | `5200` | `/api/v1/admins/*` -> `:5100` |
| merchant-user | `5300` | `/api/v1/merchants/*` -> `:5100` |

**เข้าใช้งานผ่าน port ของ FE เสมอ (`:5200` / `:5300`) ไม่ใช่ `:5100` ตรง** — login จะพังถ้าเข้าตรง (ดู §5.2).

---

## 5. Provider SSO — Google + Microsoft Entra ID (Dev)

OIDC เป็น provider-scoped ทั้งสองฝั่ง (`multi-provider-oidc`) — คนละ scheme, คนละ callback ต่อ provider,
ไม่ปนกัน:

| | section ใน `appsettings.Development.json` | scheme | login | callback |
|---|---|---|---|---|
| Admin / Google | `AdminAuth:Providers:Google` | `AdminGoogle` | `/api/v1/admins/auth/google/login` | `/api/v1/admins/auth/google/callback` |
| Admin / Microsoft | `AdminAuth:Providers:Microsoft` | `AdminMicrosoft` | `/api/v1/admins/auth/microsoft/login` | `/api/v1/admins/auth/microsoft/callback` |
| Merchant-user / Google | `MerchantAuth:Providers:Google` | `MerchantUserGoogle` | `/api/v1/merchants/auth/google/login` | `/api/v1/merchants/auth/google/callback` |
| Merchant-user / Microsoft | `MerchantAuth:Providers:Microsoft` | `MerchantUserMicrosoft` | `/api/v1/merchants/auth/microsoft/login` | `/api/v1/merchants/auth/microsoft/callback` |

`{provider}` ใน login route รับแค่ `google`/`microsoft` — provider ที่ไม่รู้จักหรือไม่ได้ config (`ClientId`
ว่าง) ตอบ **404** (ไม่ใช่ 409 เหมือนเดิม). callback path ไม่ใช่ mapped endpoint — เป็น `CallbackPath` ของ OIDC
middleware เอง.

merchant-user `register`/`me` ยังอยู่ที่ `/api/v1/merchants/users/register` และ `/api/v1/merchants/users/me`
เหมือนเดิม — ย้ายเฉพาะ auth (`login`/`callback`/`logout`/`logout-all`) ออกมาที่ `/api/v1/merchants/auth/**`.

### 5.1 ตั้งค่า Merchant-user OIDC

`appsettings.Development.json` (gitignored) section `MerchantAuth:Providers:Google` (rename จาก
`MerchantUser:Oidc` -> `MerchantUserAuth` ใน multi-provider-oidc แล้ว rename ซ้ำเป็น `MerchantAuth` ใน
PR #135 — เครื่อง dev ที่ตั้ง section `MerchantUserAuth` ไว้แล้วต้อง rename เอง ไม่งั้น provider หาย,
login ตอบ 404):

```json
"MerchantAuth": {
  "Providers": {
    "Google": {
      "ClientId": "<merchant-user-google-client-id>.apps.googleusercontent.com",
      "ClientSecret": "<merchant-user-google-client-secret>"
    },
    "Microsoft": {
      "ClientId": "<merchant-user-entra-client-id>",
      "ClientSecret": "<merchant-user-entra-client-secret>"
    }
  },
  "RegisterUrl": "http://localhost:5300/register"
}
```

`Authority`/`CallbackPath`/`HostedDomain`/`ErrorPath` มี default ที่สมเหตุสมผลอยู่แล้ว ต่อ provider —
Google callback default = `/api/v1/merchants/auth/google/callback`, Microsoft callback default =
`/api/v1/merchants/auth/microsoft/callback` (`Authority` merchant-Microsoft default =
`https://login.microsoftonline.com/organizations/v2.0`, multi-tenant org). **ต้องอัปเดต authorized redirect
URI ที่ IdP ก่อน deploy** ดู §5.2. override เฉพาะเมื่อ deploy ต่างไปจาก dev มาตรฐาน.

**สำคัญ:** ถ้า provider ใดว่าง `ClientId` -> scheme ของ provider นั้น (เช่น `MerchantUserGoogle`) จะถูก skip
ทั้งตัว (REQ-14.2, กันไม่ให้ OIDC ที่ config ไม่ครบทำ API ล่มทั้งระบบ). ผลคือ
`GET /api/v1/merchants/auth/google/login` ตอบ **404** (provider ไม่พร้อม, ดู §7).

### 5.2 IdP Console — redirect URI ต้องเป็น **origin ของ FE proxy** ไม่ใช่ `:5100`

> **สำคัญที่สุดในหน้านี้.** ลงทะเบียน redirect URI ผิด port = login พังทุกครั้ง (`redirect_uri_mismatch`)
> และ CI ตรวจไม่ได้ (contract อยู่นอก repo).

**ทำไมเป็น port ของ FE:** FE dev server proxy `/api/v1/*` ไปที่ backend และส่ง `X-Forwarded-Host` /
`X-Forwarded-Proto` มาด้วย. `src/Hosts/Api/Program.cs` เรียก `app.UseForwardedHeaders(...)` **เป็น
middleware แรกสุด** (`ForwardedHeaders.XForwardedHost | XForwardedProto`) เพื่อให้ทุกอย่างข้างล่าง —
รวมถึงตัวประกอบ `redirect_uri` ของ OIDC — เห็น host ที่ **browser** ใช้จริง ไม่ใช่ host ของ process นี้.
browser อยู่บน `:5200`/`:5300` -> `redirect_uri` ที่ backend ส่งไป IdP ก็เป็น `:5200`/`:5300` ตาม.
(default trust = loopback ซึ่งครอบ dev proxy อยู่แล้ว.)

ลงทะเบียนตามนี้:

| client | Authorized redirect URI |
|---|---|
| Admin / Google | `http://localhost:5200/api/v1/admins/auth/google/callback` |
| Admin / Microsoft | `http://localhost:5200/api/v1/admins/auth/microsoft/callback` |
| Merchant-user / Google | `http://localhost:5300/api/v1/merchants/auth/google/callback` |
| Merchant-user / Microsoft | `http://localhost:5300/api/v1/merchants/auth/microsoft/callback` |

**Google Cloud Console** — OAuth 2.0 Client ID (คนละตัวสำหรับ admin กับ merchant-user) -> Authorized
redirect URIs: ใส่ 2 บรรทัด Google ข้างบน (ตัวละ client).

**Microsoft Entra ID** — ต้องสร้าง **app registration ใหม่ 2 ตัว** (admin = single-tenant,
merchant-user = multi-tenant/organizational accounts) — ไม่ share client กับ Google. Redirect URI type =
**Web**, ใส่ 2 บรรทัด Microsoft ข้างบน (ตัวละ registration).

> `:5100` **ไม่ต้องลงทะเบียน** และเข้าตรงไม่ได้: ยิง login ที่ `:5100` โดยตรงจะไม่มี `X-Forwarded-Host` ->
> `redirect_uri` กลายเป็น `http://localhost:5100/...` ซึ่งไม่มีใน console -> IdP ตอบ
> `Error 400: redirect_uri_mismatch`. ให้เข้าผ่าน FE console เสมอ (§4.4).
> cross-check: `docs/reference/admins.md` (หัวข้อ Proxy / Dev-CORS) ระบุตรงกัน — Next.js rewrites
> ส่ง `X-Forwarded-Host` เอง, backend honor แล้ว, `redirect_uri` ออกมาเป็น origin ของ FE.

ทั้งสอง app registration ต้องเพิ่ม **optional claim `email`** ที่ id_token (Entra ID **ไม่ส่ง** `email` claim
โดย default และไม่ส่ง `email_verified` เลย). subject ของ Microsoft คือ claim `oid` (ไม่ใช่ `sub`) — ค่าที่เก็บใน
`ExternalLogins.Provider` เป็น `"google"`/`"microsoft"`.

> **Cutover สำคัญ (multi-provider-oidc):** redirect URI ย้ายจากรูปแบบเดิมที่ไม่มี provider segment
> (`/api/v1/admins/auth/callback`, `/api/v1/merchants/users/auth/callback`) เป็น provider-scoped ด้านบน —
> contract นี้อยู่ **นอก repo** (IdP console), CI ไม่ตรวจให้. ต้องอัปเดต redirect URIs ก่อน deploy branch นี้ใน
> ทุก environment ไม่งั้น login พังทันที (`Error 400: redirect_uri_mismatch` ฝั่ง Google, error เทียบเท่าฝั่ง
> Entra) แม้ CI เขียว.

### 5.3 ตรวจว่าพร้อม

```
curl -s -o /dev/null -D - "http://localhost:5100/api/v1/merchants/auth/google/login?returnTo=/register" \
  | grep -iE "^HTTP|^location"
```

ถูก = `302 Found` + `Location: https://accounts.google.com/...redirect_uri=...%2Fmerchants%2Fauth%2Fgoogle%2Fcallback`.
provider `microsoft` เช็คแบบเดียวกันที่ path `/api/v1/merchants/auth/microsoft/login`.

> curl นี้เช็คแค่ว่า **provider ถูก config แล้ว** (302 ไม่ใช่ 404) เท่านั้น. เพราะยิงตรง `:5100` ไม่มี
> `X-Forwarded-Host` -> `redirect_uri` ใน `Location` จะเป็น `localhost%3A5100` ซึ่ง **ไม่ใช่ค่าที่ลงทะเบียน**
> (§5.2) — กด link นั้นต่อจะเจอ `redirect_uri_mismatch` เป็นเรื่องปกติ ไม่ใช่ bug. ทดสอบ login จริงต้องผ่าน
> `:5300` (merchant-user) / `:5200` (admin). อยากเห็น redirect_uri ของจริง เติม header เอง:
> `curl -H 'X-Forwarded-Host: localhost:5300' -H 'X-Forwarded-Proto: http' ...`

### 5.4 Flow ที่คาดหวัง (merchant-user / ตัวแทน)

`:5300/login` -> ปุ่มเลือก provider -> `/api/v1/merchants/auth/{google|microsoft}/login` -> IdP -> callback ->
branch 4 ทาง (`ResolveLogin`):
- **NotFound** (subject ใหม่) -> mint registration ticket -> redirect `:5300/register?ticket=...`
- **PendingApproval** -> 403 "awaiting approval"
- **Rejected** -> correction ticket -> `:5300/register?ticket=...`
- **Active** -> เปิด session cookie -> redirect returnTo

---

## 6. รันเทส

solution file = `pol-core.slnx`.

```
# Unit (เร็ว, ไม่ต้องใช้ DB):
dotnet test pol-core.slnx --filter "Category!=Integration"

# Integration (ต้องมี pol-db :11433 ขึ้นแล้ว + migrate แล้ว — §2.2/§2.3 — ก่อน dotnet test เสมอ):
source .env.integration
dotnet test pol-core.slnx --filter "Category=Integration"
```

> **rf1 (2026-07-12): ไม่มี container แยกสำหรับ integration อีกแล้ว.** เดิม `.env.integration` ชี้ container แยกที่
> `:11434` (ตั้งขึ้นเองนอก `docker-compose.yml`, ไม่เคย reproducible จริง) — ตอนนี้ integration suite ชี้ `pol-db`/
> `:11433` **ตัวเดียวกับ dev** เสมอ (ยืนยันจาก `docker-compose.yml` + `.github/workflows/ci.yml`: มี SQL Server
> service เดียว, ไม่มี `:11434` ที่ไหนเลย). `IntegrationDb.cs` fallback `POL_SQL_SERVER`/`POL_DB` เป็น
> `localhost,11433`/`VCentralPay` อยู่แล้ว (ตรงกับ `.env`) — `.env.integration` (gitignored, **ไม่มีใน fresh clone
> ต้องสร้างเอง**) มีไว้ export **รหัสผ่าน 2 ตัว** ที่ไม่มี fallback เท่านั้น (`dotnet test` เป็น subprocess อ่าน env
> ตรงจาก shell ไม่ใช่จากไฟล์ `.env`). `IntegrationDb.cs` อ่าน env แค่ 4 ตัว — `POL_SQL_SERVER`, `POL_DB`,
> `POL_SA_PASSWORD`, `POL_APP_PASSWORD` (task 8 ยุบ `pol_admin`/`pol_worker`/`pol_resolver`/`pol_vault_auditor`
> เข้า `pol_app`; ทุกเทสรันบน principal เดียวนั้น + `sa` เฉพาะ vault-audit applock).
> สร้าง `.env.integration` ด้วย exports ตามนี้ (ค่าเดียวกับ `.env`):

```
export POL_SQL_SERVER='localhost,11433'
export POL_DB='VCentralPay'
export POL_SA_PASSWORD='<sa-pwd>'
export POL_APP_PASSWORD='<pol_app-pwd>'
export POL_DESIGN_SQL="Server=localhost,11433;Database=VCentralPay;User Id=sa;Password=<sa-pwd>;Encrypt=True;TrustServerCertificate=True"
```

> Integration suite ตั้ง `Pooling=False` (fresh physical connection ทุก open) — เดิมจำเป็นสมัย RLS ที่ผูก
> `SESSION_CONTEXT` กับ connection, ตอนนี้ไม่มีอะไรเขียน `SESSION_CONTEXT` แล้วจึงเหลือไว้เพราะ connection สด
> ต่อเทสอ่านง่ายกว่าเฉย ๆ. ทุกเทสชี้ container เดียวกัน — อย่าเผลอสร้าง container ใหม่ที่พอร์ตอื่นสำหรับ integration.

CI gate: unit + integration ต้องเขียวก่อน merge (required check).

---

## 7. Troubleshooting (ปัญหาที่เจอจริง)

### `GET /api/v1/merchants/auth/google/login` ตอบ 404 (ไม่ใช่ 302)

**สาเหตุ:** `MerchantAuth:Providers:Google:ClientId` ว่าง/ไม่มี section -> scheme `MerchantUserGoogle`
ไม่ถูก register -> route มองว่า provider นี้ไม่พร้อม/ไม่รู้จัก -> 404 (เดิมสมัย single-provider ตอบ 409 —
provider-scoped route แยกให้ตรงตัวว่า "provider นี้" หายไป ไม่ใช่ merchant-user auth ทั้งระบบล่ม).
**แก้:** ตั้ง `MerchantAuth:Providers:Google` ครบ (§5.1) แล้ว **restart API เต็ม** (config change ไม่
hot-reload). provider `microsoft` เช็คแบบเดียวกันที่ `MerchantAuth:Providers:Microsoft`.

### `Error 400: redirect_uri_mismatch` ที่หน้า Google / เทียบเท่าฝั่ง Microsoft Entra

**สาเหตุที่พบบ่อยที่สุด:** เปิด browser ไปที่ `:5100` ตรง แทนที่จะผ่าน FE console. ไม่ผ่าน proxy = ไม่มี
`X-Forwarded-Host` -> backend ประกอบ `redirect_uri` เป็น `http://localhost:5100/...` ซึ่งไม่ได้ลงทะเบียนไว้.
**แก้:** เข้าผ่าน `http://localhost:5300` (merchant-user) / `http://localhost:5200` (admin) — §4.4.

**สาเหตุอื่น:** ยังไม่ได้ลงทะเบียน URI ของ proxy origin, หรือแก้คนละ client กับที่
`MerchantAuth:Providers:{Google|Microsoft}:ClientId` ชี้.
**แก้:** §5.2 — เพิ่ม `http://localhost:5300/api/v1/merchants/auth/google/callback` (หรือ
`.../microsoft/callback`; ฝั่ง admin ใช้ `http://localhost:5200/api/v1/admins/auth/{provider}/callback`)
ที่ client ตัวที่ถูก. ดู client ที่ backend ใช้จริงด้วย `curl` (§5.3) เทียบ prefix ของ `client_id`.

### callback redirect ไป `login-error?reason=ticket-issue-failed`

**สาเหตุ:** INSERT ถูกปฏิเสธบนตาราง merchant-user identity — `pol_app` ไม่มี grant.

```
SqlException 229: The INSERT permission was denied on the object 'MerchantUsers'
```

ตรวจ grant ปัจจุบัน (ดู §8). ตารางที่ merchant-user identity/registration ต้องการ (`pol_app`)
ตามชื่อบน `develop` (schema `merch`, rf1 rename 2026-07-12):
`MerchantUsers` (incl. person details + คอลัมน์ `MerchantId` แทน assignment table แยก), `ExternalLogins`,
`RegistrationAudits`, `MerchantUserSessions`, `MerchantAuthAudits`,
`MerchantUserRoleDefinitions` / `MerchantUserRoles` / `MerchantUserRolePermissions`.

> หมายเหตุ: PR #30 (account control-plane parity) rename `TenantUsers` -> `ProducerAccounts` + เพิ่ม
> `ProducerTenantAssignments`; rf1 (2026-07-12) rename อีกชั้น `ProducerAccounts` -> `MerchantUsers` และ
> ดูดซับ `ProducerTenantAssignments` เป็นคอลัมน์ `MerchantId` ตรง (ตารางแยกถูก drop). ใช้ชื่อล่าสุดเสมอ.

grant อยู่ใน EF migration อยู่แล้ว -> **fresh DB / CI / prod ถูกต้องเสมอ**. ถ้าขาดบน dev DB =
drift (เช่นตารางถูก drop/recreate ข้าม migration หลายรอบ). re-apply ตรงด้วย migration หรือ
GRANT ตามที่ migration กำหนด.

### API ตอบ 500 ที่ proxy `:5200` แต่ direct `:5100` ต่อไม่ได้

`curl :5100` ได้ `000` = backend ไม่ขึ้น. proxy FE คืน 500 เพราะ connection refused. รัน API (§4.2).

### Hosts.Tests ใหม่ไปแตะ dev DB `:11433` (ควรเป็นเทสที่ไม่ต้องมี DB)

`WebApplicationFactory` boot API ใน Development ซึ่ง**อ่าน `ConnectionStrings:Migrator` จาก
`appsettings.Development.json` แล้ว auto-migrate** (`Program.cs`) -> หลาย factory boot ขนานกันจะยิง
migration ใส่ dev DB ตัวเดียวกันพร้อมกัน. ทุก factory ใน `tests/Hosts.Tests/` จึงต้อง blank คีย์นั้นทิ้ง:

```csharp
builder.UseEnvironment(Environments.Development);
builder.UseSetting("ConnectionStrings:Migrator", "");
```

เขียนเทสใหม่แล้วลืมบรรทัดหลัง = เทสไปแตะ DB จริง. ก็อป pattern จากไฟล์ที่มีอยู่ (เช่น
`AdminAuthLoginRedirectTests.cs`).

---

## 8. DB inspection cheatsheet

query ตรงผ่าน container (sa). ค่า password อ่านจาก `.env` — แทน `<sa-pwd>`:

```
# grants ของ pol_app (principal เดียวที่มี) ทุกตาราง schema merch — catalog VCentralPay แยกหลาย schema
# ตัวอย่างนี้เจาะ merch, เปลี่ยนเป็น 'admin'/'shop'/'txn'/'iam' ตามที่ต้องการตรวจ:
docker exec pol-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P '<sa-pwd>' -C -d VCentralPay -W -Q "
SELECT o.name, STRING_AGG(dp.permission_name,',')
FROM sys.database_permissions dp
JOIN sys.objects o ON dp.major_id=o.object_id
WHERE dp.grantee_principal_id=USER_ID('pol_app')
  AND SCHEMA_NAME(o.schema_id)='merch'
GROUP BY o.name ORDER BY o.name;"

# เช็ค principal มีสิทธิ์ INSERT บนตารางหนึ่งไหม:
... -Q "EXECUTE AS USER='pol_app';
        SELECT HAS_PERMS_BY_NAME('merch.Users','OBJECT','INSERT');
        REVERT;"

# unique index (เงื่อนไข dedup registration) = UNIQUE บน MerchantUsers.Subject
# (person details + name/photo อยู่บนตารางนี้ด้วยแล้ว หลัง AddProducerAccountDetailsDropProfile;
# rename ProducerAccounts -> MerchantUsers ใน rf1):
... -Q "SELECT i.name, i.is_unique FROM sys.indexes i
        JOIN sys.tables t ON i.object_id=t.object_id
        WHERE SCHEMA_NAME(t.schema_id)='merch' AND t.name='MerchantUsers';"
```

> dev DB และ integration test ใช้ container/port เดียวกันแล้ว (`11433`, rf1) — ไม่มี `11434` ให้ต้องระวังอีกต่อไป.

---

## 9. อ้างอิง

- `docs/reference/producer-module.md` — สเปก producer SSO module เต็ม (pre-rf1 vocab — ดู stale banner ในไฟล์, actor จริงตอนนี้คือ `MerchantUser`)
- `docs/reference/admins.md` — admin SSO + FE integration
- `.ai/shared/ARCHITECTURE.md` — folder layout, principal model
- `.ai/shared/CODING_STANDARDS.md` — version pin, hard constraints
- `docker-compose.yml` / `docker/bootstrap/01-principals.sql` — dev DB + principals
