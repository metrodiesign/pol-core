# คู่มือการรัน pol-core (Local Dev)

คู่มือรันสำหรับทีมพัฒนา ครอบคลุม first-time setup, รันประจำวัน (DB / API / Worker),
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
- `MSSQL_SA_PASSWORD`
- `POL_APP_PASSWORD`, `POL_ADMIN_PASSWORD`, `POL_WORKER_PASSWORD` — strong, **ห้ามมีชื่อ login อยู่ในรหัส** (SQL Server password policy)
- `ConnectionStrings__*` — ใส่รหัสให้ตรงกับ 3 ตัวบน
- `POL_DESIGN_SQL` — ใช้ `sa` (migration ต้องมีสิทธิ์ DDL ที่ app principal ไม่มี)
- `Vault__MasterKeyBase64` — gen ของจริง local: `head -c 32 /dev/urandom | base64`

เปิด git hooks (enforcement floor):

```
git config core.hooksPath .githooks
```

### 2.2 ยก DB + สร้าง principals

```
docker compose up -d
```

ทำ 2 อย่าง:
- `pol-db` — SQL Server 2025 ที่ `localhost:11433`
- `pol-db-init` — รัน `docker/bootstrap/01-principals.sql` (idempotent): สร้าง DB `VCentralPay`,
  logins `pol_app` / `pol_admin` / `pol_worker`, role `pol_rls_bypass`. exit 0 เมื่อเสร็จ

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
>   principal `pol_app` เดิม; Admin/Worker/`POL_DESIGN_SQL` ไม่เปลี่ยน); `Tenant:DevTenantId` -> `Merchant:DevMerchantId`;
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
| API (`src/Hosts/Api`) | `5100` (http) / `5101` (https) | `pol_app` (default), `pol_admin` (keyed) | REST + BFF auth |
| Worker (`src/Hosts/Worker`) | console (ไม่มี port) | `pol_worker` | outbox dispatcher |
| FE `pol-admin` (repo แยก) | `5200` | — | Next.js, proxy `/admin/*` + `/merchant-users/*` ไป `:5100` |

Connection strings (ASP.NET map `ConnectionStrings__<Name>` -> `ConnectionStrings:<Name>`):

| Name | login | สิทธิ์ |
|---|---|---|
| `App` | `pol_app` | RLS-enforced (merchant data plane) |
| `Admin` | `pol_admin` | control-plane (admin + merchant-user identity) — **rf1: ไม่ใช่ RLS-bypass แล้ว**, scoped-admin ถูกบังคับที่ RLS floor เอง (tier-check ใน `sec.fn_merchant_predicate`) |
| `Worker` | `pol_worker` | outbox |
| `Migrator` | `sa` | DDL — auto-migrate ตอน boot (Dev เท่านั้น) |

> หลักการ principal: API รัน 3 ระนาบ — `pol_app` สำหรับ funnel routes (RLS), keyed `pol_admin`
> สำหรับ admin + merchant-user identity (control-plane), แยกขาดกัน. ดู `.ai/shared/ARCHITECTURE.md`.

---

## 4. รันประจำวัน

### 4.1 DB (ถ้ายังไม่ขึ้น)

```
docker compose up -d
```

### 4.2 API

```
dotnet watch --project src/Hosts/Api/Api.csproj run
```

รอจนเห็น `Now listening on: http://localhost:5100`. Dev จะ auto-migrate ก่อน (log:
`Applied pending EF migrations`). hot reload จับการแก้ code อัตโนมัติ — **แต่การแก้ config
(`appsettings.*.json`) หรือ DI ต้อง restart เต็ม**.

> เปิดค้างใน terminal แยกของคุณเอง (หรือ tab IDE). อย่ารันผ่าน agent background — มันถูก
> kill ตอนจบ turn.

### 4.3 Worker (เมื่อต้องทดสอบ outbox/event)

```
dotnet run --project src/Hosts/Worker/Worker.csproj
```

### 4.4 FE (pol-admin — repo แยก)

รันตาม README ของ repo `pol-admin`. ตั้ง `ADMIN_API_ORIGIN=http://localhost:5100` (proxy
ทั้ง `/admin/*` และ `/merchant-users/*` ไป host เดียว). เปิดที่ `http://localhost:5200`.

---

## 5. Google SSO (Dev)

มี OIDC client แยก 2 ตัว (คนละ scheme, คนละ callback — ไม่ปนกัน):

| | section ใน `appsettings.Development.json` | scheme | callback |
|---|---|---|---|
| Admin | `Google:Oidc` | `Google` | `/api/v1/admins/auth/callback` |
| Merchant-user | `MerchantUser:Oidc` | `MerchantUserGoogle` | `/api/v1/merchant-users/auth/callback` |

### 5.1 ตั้งค่า Merchant-user OIDC

`appsettings.Development.json` (gitignored) section `MerchantUser:Oidc` (rename จาก `Producer:Oidc`, rf1):

```json
"MerchantUser": {
  "Oidc": {
    "ClientId": "<merchant-user-google-client-id>.apps.googleusercontent.com",
    "ClientSecret": "<merchant-user-google-client-secret>",
    "RegisterUrl": "http://localhost:5200/register"
  }
}
```

`Authority`/`CallbackPath`/`HostedDomain`/`ErrorPath` มี default ที่สมเหตุสมผลอยู่แล้ว (`CallbackPath` default =
`/api/v1/merchant-users/auth/callback`) — override เฉพาะเมื่อ deploy ต่างไปจาก dev มาตรฐาน.

**สำคัญ:** ถ้า `ClientId` ว่าง -> scheme `MerchantUserGoogle` (เดิม `ProducerGoogle`) จะถูก skip ทั้งตัว (REQ-14.2,
กันไม่ให้ OIDC ที่ config ไม่ครบทำ API ล่มทั้งระบบ). ผลคือ `GET /api/v1/merchant-users/auth/login` ตอบ **409** แทน 302
(ดู §7).

### 5.2 Google Cloud Console

ที่ OAuth 2.0 Client ID ของ merchant-user -> **Authorized redirect URIs** ลงทะเบียน **ทั้งสอง**:

```
http://localhost:5100/api/v1/merchant-users/auth/callback
http://localhost:5200/api/v1/merchant-users/auth/callback
```

ทำไมต้องสองตัว: redirect_uri ที่ handler ส่งให้ Google สร้างจาก `Request.Host` ของ request.
- ยิง backend ตรง (`:5100`) -> redirect_uri = `:5100/...`
- ผ่าน FE proxy (`:5200/api/v1/merchant-users/auth/login`) — API ตั้ง `UseForwardedHeaders`
  (`X-Forwarded-Host`, ดู `Program.cs`) -> **ถ้า** proxy IP อยู่ใน `ForwardedHeaders:KnownNetworks`/
  `KnownProxies` -> `Request.Host` = `:5200` -> redirect_uri = `:5200/...`

dev default ไม่ตั้ง KnownProxies -> forwarded host ถูก ignore -> ได้ `:5100`. แต่ลงทะเบียนทั้งคู่
ครอบทั้ง 2 เส้นทาง กัน `Error 400: redirect_uri_mismatch`. (scheme/host/port/path ต้องตรงเป๊ะ,
ไม่มี trailing slash.)

### 5.3 ตรวจว่าพร้อม

```
curl -s -o /dev/null -D - "http://localhost:5100/api/v1/merchant-users/auth/login?returnTo=/register" \
  | grep -iE "^HTTP|^location"
```

ถูก = `302 Found` + `Location: https://accounts.google.com/...redirect_uri=...%2Fmerchant-users%2Fauth%2Fcallback`.

### 5.4 Flow ที่คาดหวัง (merchant-user / ตัวแทน)

`:5200/login` -> ปุ่มตัวแทน -> `/api/v1/merchant-users/auth/login` -> Google -> callback -> branch 4 ทาง
(`ResolveLogin`):
- **NotFound** (subject ใหม่) -> mint registration ticket -> redirect `:5200/register?ticket=...`
- **PendingApproval** -> 403 "awaiting approval"
- **Rejected** -> correction ticket -> `:5200/register?ticket=...`
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
> ต้องสร้างเอง**) มีไว้ export รหัสผ่าน 4 ตัวที่ไม่มี fallback เท่านั้น (`dotnet test` เป็น subprocess อ่าน env ตรงจาก
> shell ไม่ใช่จากไฟล์ `.env`). สร้าง `.env.integration` ด้วย exports ตามนี้ (ค่าเดียวกับ `.env`):

```
export POL_SQL_SERVER='localhost,11433'
export POL_DB='VCentralPay'
export POL_SA_PASSWORD='<sa-pwd>'
export POL_APP_PASSWORD='<pol_app-pwd>'
export POL_ADMIN_PASSWORD='<pol_admin-pwd>'
export POL_WORKER_PASSWORD='<pol_worker-pwd>'
export POL_DESIGN_SQL="Server=localhost,11433;Database=VCentralPay;User Id=sa;Password=<sa-pwd>;Encrypt=True;TrustServerCertificate=True"
```

> Integration suite ส่วนใหญ่ปิด pooling (fresh physical connection ทุก open) เพื่อพิสูจน์ RLS ตรง connection;
> `PooledConnectionReuseTests` (task 4, rf1) เปิด pooling เฉพาะจุดเพื่อพิสูจน์ stale actor context ไม่รอดข้าม
> pooled reuse. ทั้งคู่ชี้ container เดียวกัน — อย่าเผลอสร้าง container ใหม่ที่พอร์ตอื่นสำหรับ integration.

CI gate: unit + integration ต้องเขียวก่อน merge (required check).

---

## 7. Troubleshooting (ปัญหาที่เจอจริง)

### `GET /api/v1/merchant-users/auth/login` ตอบ 409 (ไม่ใช่ 302)

```json
{"title":"The operation is not allowed in the resource's current state","status":409}
```

**สาเหตุ:** `MerchantUser:Oidc:ClientId` ว่าง/ไม่มี section -> scheme `MerchantUserGoogle` (เดิม
`ProducerGoogle`) ไม่ถูก register -> challenge โยน `InvalidOperationException: No authentication
handler is registered for the scheme 'MerchantUserGoogle'` -> global handler map เป็น 409.
**แก้:** ตั้ง `MerchantUser:Oidc` ครบ (§5.1) แล้ว **restart API เต็ม** (config change ไม่ hot-reload).

### `Error 400: redirect_uri_mismatch` ที่หน้า Google

**สาเหตุ:** redirect URI ที่ backend ส่ง ไม่ตรงกับที่ลงทะเบียนใน Google client (หรือแก้คนละ client
กับที่ `MerchantUser:Oidc:ClientId` ชี้).
**แก้:** §5.2 — เพิ่ม `http://localhost:5100/api/v1/merchant-users/auth/callback` ที่ client ตัวที่ถูก.
ดู client ที่ backend ใช้จริงด้วย `curl` (§5.3) เทียบ prefix ของ `client_id`.

### callback redirect ไป `login-error?reason=ticket-issue-failed`

**สาเหตุ:** INSERT ถูกปฏิเสธบนตาราง merchant-user identity — `pol_admin` ไม่มี grant.

```
SqlException 229: The INSERT permission was denied on the object 'MerchantUsers'
```

ตรวจ grant ปัจจุบัน (ดู §8). ตารางที่ merchant-user identity/registration ต้องการ (`pol_admin`)
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

### Hosts.Tests fail หมู่ ~30 ตัว ("policy does not contain a predicate ...")

WebApplicationFactory boot API ใน Development -> auto-migrate ชน dev DB `:11433` ที่ใช้ร่วมกัน
แบบขนาน. `ALTER SECURITY POLICY` ไม่ rollback กับ migration tx. migration ที่แตะ RLS predicate
ต้อง guard ด้วย `IF (NOT) EXISTS` ให้ retry เป็น no-op. ถ้า DB ค้าง: re-add predicate ที่หาย แล้ว
`dotnet ef database update` แบบ single-thread.

---

## 8. DB inspection cheatsheet

query ตรงผ่าน container (sa). ค่า password อ่านจาก `.env` — แทน `<sa-pwd>`:

```
# grants ของ pol_admin ทุกตาราง schema merch (rf1: catalog VCentralPay เดิม แต่แยกหลาย schema แล้ว —
# ตัวอย่างนี้เจาะ merch, เปลี่ยนเป็น 'admin'/'shop'/'txn' ตามที่ต้องการตรวจ):
docker exec pol-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P '<sa-pwd>' -C -d VCentralPay -W -Q "
SELECT o.name, STRING_AGG(dp.permission_name,',')
FROM sys.database_permissions dp
JOIN sys.objects o ON dp.major_id=o.object_id
WHERE dp.grantee_principal_id=USER_ID('pol_admin')
  AND SCHEMA_NAME(o.schema_id)='merch'
GROUP BY o.name ORDER BY o.name;"

# เช็ค principal มีสิทธิ์ INSERT บนตารางหนึ่งไหม:
... -Q "EXECUTE AS USER='pol_admin';
        SELECT HAS_PERMS_BY_NAME('merch.MerchantUsers','OBJECT','INSERT');
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
- `docs/reference/admin-module.md` — admin SSO + FE integration
- `.ai/shared/ARCHITECTURE.md` — folder layout, principal model
- `.ai/shared/CODING_STANDARDS.md` — version pin, hard constraints
- `docker-compose.yml` / `docker/bootstrap/01-principals.sql` — dev DB + principals
