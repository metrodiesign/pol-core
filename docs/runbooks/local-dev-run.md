# คู่มือการรัน pol-core (Local Dev)

คู่มือรันสำหรับทีมพัฒนา ครอบคลุม first-time setup, รันประจำวัน (DB / API / Worker),
การตั้งค่า Google SSO (Admin + Producer), การรันเทส, และ troubleshooting ของปัญหาที่เจอจริง.

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
- `pol-db-init` — รัน `docker/bootstrap/01-principals.sql` (idempotent): สร้าง DB `PaymentOrchestration`,
  logins `pol_app` / `pol_admin` / `pol_worker`, role `pol_rls_bypass`. exit 0 เมื่อเสร็จ

> object-level GRANT/DENY ไม่ได้อยู่ในไฟล์นี้ — มันอยู่ใน EF migration (หลังตารางถูกสร้าง).

### 2.3 รัน migrations

Dev: API auto-migrate ตอน boot ถ้า `ConnectionStrings:Migrator` ถูกตั้ง (ดู §4). หรือรันเองตรง:

```
POL_DESIGN_SQL="<sa conn string>" \
dotnet ef database update --context ProducerDbContext \
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

---

## 3. Topology (ports / principals / connection strings)

| host | port | principal | ใช้ทำอะไร |
|---|---|---|---|
| SQL Server (dev) | `11433` | — | DB หลัก `PaymentOrchestration` |
| SQL Server (integration test) | `11434` | — | DB แยกสำหรับ Integration suite (ดู `.env.integration`) |
| API (`src/Hosts/Api`) | `5100` (http) / `5101` (https) | `pol_app` (default), `pol_admin` (keyed) | REST + BFF auth |
| Worker (`src/Hosts/Worker`) | console (ไม่มี port) | `pol_worker` | outbox dispatcher |
| FE `pol-admin` (repo แยก) | `5200` | — | Next.js, proxy `/admin/*` + `/producer/*` ไป `:5100` |

Connection strings (ASP.NET map `ConnectionStrings__<Name>` -> `ConnectionStrings:<Name>`):

| Name | login | สิทธิ์ |
|---|---|---|
| `Producer` | `pol_app` | RLS-enforced (tenant data plane) |
| `Admin` | `pol_admin` | RLS-bypass / control-plane (admin + producer identity) |
| `Worker` | `pol_worker` | outbox |
| `Migrator` | `sa` | DDL — auto-migrate ตอน boot (Dev เท่านั้น) |

> หลักการ principal: API รัน 3 ระนาบ — `pol_app` สำหรับ tenant routes (RLS), keyed `pol_admin`
> สำหรับ admin + producer identity (control-plane), แยกขาดกัน. ดู `.ai/shared/ARCHITECTURE.md`.

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
ทั้ง `/admin/*` และ `/producer/*` ไป host เดียว). เปิดที่ `http://localhost:5200`.

---

## 5. Google SSO (Dev)

มี OIDC client แยก 2 ตัว (คนละ scheme, คนละ callback — ไม่ปนกัน):

| | section ใน `appsettings.Development.json` | scheme | callback |
|---|---|---|---|
| Admin | `Google:Oidc` | `Google` | `/admin/auth/callback` |
| Producer | `Producer:Oidc` | `ProducerGoogle` | `/producer/auth/callback` |

### 5.1 ตั้งค่า Producer OIDC

`appsettings.Development.json` (gitignored) section `Producer:Oidc`:

```json
"Producer": {
  "Oidc": {
    "Authority": "https://accounts.google.com",
    "ClientId": "<producer-google-client-id>.apps.googleusercontent.com",
    "ClientSecret": "<producer-google-client-secret>",
    "CallbackPath": "/producer/auth/callback",
    "HostedDomain": "",
    "ErrorPath": "http://localhost:5200/login-error",
    "RegisterUrl": "http://localhost:5200/register"
  }
}
```

**สำคัญ:** ถ้า `ClientId` ว่าง -> scheme `ProducerGoogle` จะถูก skip ทั้งตัว (REQ-14.2, กันไม่ให้
OIDC ที่ config ไม่ครบทำ API ล่มทั้งระบบ). ผลคือ `GET /producer/auth/login` ตอบ **409** แทน 302
(ดู §7).

### 5.2 Google Cloud Console

ที่ OAuth 2.0 Client ID ของ producer -> **Authorized redirect URIs** ลงทะเบียน **ทั้งสอง**:

```
http://localhost:5100/producer/auth/callback
http://localhost:5200/producer/auth/callback
```

ทำไมต้องสองตัว: redirect_uri ที่ handler ส่งให้ Google สร้างจาก `Request.Host` ของ request.
- ยิง backend ตรง (`:5100`) -> redirect_uri = `:5100/...`
- ผ่าน FE proxy (`:5200/producer/auth/login`) — API ตั้ง `UseForwardedHeaders`
  (`X-Forwarded-Host`, ดู `Program.cs`) -> **ถ้า** proxy IP อยู่ใน `ForwardedHeaders:KnownNetworks`/
  `KnownProxies` -> `Request.Host` = `:5200` -> redirect_uri = `:5200/...`

dev default ไม่ตั้ง KnownProxies -> forwarded host ถูก ignore -> ได้ `:5100`. แต่ลงทะเบียนทั้งคู่
ครอบทั้ง 2 เส้นทาง กัน `Error 400: redirect_uri_mismatch`. (scheme/host/port/path ต้องตรงเป๊ะ,
ไม่มี trailing slash.)

### 5.3 ตรวจว่าพร้อม

```
curl -s -o /dev/null -D - "http://localhost:5100/producer/auth/login?returnTo=/register" \
  | grep -iE "^HTTP|^location"
```

ถูก = `302 Found` + `Location: https://accounts.google.com/...redirect_uri=...%2Fproducer%2Fauth%2Fcallback`.

### 5.4 Flow ที่คาดหวัง (producer)

`:5200/login` -> ปุ่มตัวแทน -> `/producer/auth/login` -> Google -> callback -> branch 4 ทาง
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

# Integration (ต้องมี SQL :11434 + env vars ด้านล่าง):
source .env.integration
dotnet test pol-core.slnx --filter "Category=Integration"
```

> `.env.integration` เป็น gitignored (มี secret) — **ไม่มีใน fresh clone ต้องสร้างเอง**. ถ้าไม่ตั้ง
> env เหล่านี้ helper จะ default `POL_SQL_SERVER` เป็น `localhost,11433` (ชน dev DB). สร้าง
> `.env.integration` ด้วย exports ตามนี้:

```
export POL_SA_PASSWORD='<sa-pwd-:11434>'
export POL_SQL_SERVER='localhost,11434'      # ต้องเป็น 11434 ไม่ใช่ 11433
export POL_DB='PaymentOrchestration'
export POL_APP_PASSWORD='<pol_app-pwd>'
export POL_ADMIN_PASSWORD='<pol_admin-pwd>'
export POL_WORKER_PASSWORD='<pol_worker-pwd>'
export POL_DESIGN_SQL="Server=localhost,11434;Database=PaymentOrchestration;User Id=sa;Password=<sa-pwd-:11434>;Encrypt=True;TrustServerCertificate=True"
```

> Integration suite ใช้ DB คนละตัว (`localhost:11434`) จาก dev (`:11433`) เพื่อไม่ปนกัน.
> ตั้ง container :11434 + principals เหมือน §2.2 แต่ชี้พอร์ต 11434.

CI gate: unit + integration ต้องเขียวก่อน merge (required check).

---

## 7. Troubleshooting (ปัญหาที่เจอจริง)

### `GET /producer/auth/login` ตอบ 409 (ไม่ใช่ 302)

```json
{"title":"The operation is not allowed in the resource's current state","status":409}
```

**สาเหตุ:** `Producer:Oidc:ClientId` ว่าง/ไม่มี section -> scheme `ProducerGoogle` ไม่ถูก register
-> challenge โยน `InvalidOperationException: No authentication handler is registered for the scheme
'ProducerGoogle'` -> global handler map เป็น 409.
**แก้:** ตั้ง `Producer:Oidc` ครบ (§5.1) แล้ว **restart API เต็ม** (config change ไม่ hot-reload).

### `Error 400: redirect_uri_mismatch` ที่หน้า Google

**สาเหตุ:** redirect URI ที่ backend ส่ง ไม่ตรงกับที่ลงทะเบียนใน Google client (หรือแก้คนละ client
กับที่ `Producer:Oidc:ClientId` ชี้).
**แก้:** §5.2 — เพิ่ม `http://localhost:5100/producer/auth/callback` ที่ client ตัวที่ถูก. ดู client
ที่ backend ใช้จริงด้วย `curl` (§5.3) เทียบ prefix ของ `client_id`.

### callback redirect ไป `login-error?reason=ticket-issue-failed`

**สาเหตุ:** INSERT ถูกปฏิเสธบนตาราง producer identity — `pol_admin` ไม่มี grant.

```
SqlException 229: The INSERT permission was denied on the object 'ProducerAccounts'
```

ตรวจ grant ปัจจุบัน (ดู §8). ตารางที่ producer identity/registration ต้องการ (`pol_admin`)
ตามชื่อบน `develop`:
`ProducerAccounts` (incl. person details), `ProducerTenantAssignments`, `ExternalLogins`,
`RegistrationAudits`, `ProducerSessions`, `ProducerAuthAudits`,
`ProducerRoles` / `ProducerRoleAssignments` / `ProducerRolePermissions`.

> หมายเหตุ: PR #30 (account control-plane parity) rename `TenantUsers` -> `ProducerAccounts` และ
> เพิ่ม `ProducerTenantAssignments`. หลัง #30 merge ใช้ชื่อใหม่นั้นแทน.

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
# grants ของ pol_admin ทุกตาราง schema VCentralPay:
docker exec pol-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P '<sa-pwd>' -C -d PaymentOrchestration -W -Q "
SELECT o.name, STRING_AGG(dp.permission_name,',')
FROM sys.database_permissions dp
JOIN sys.objects o ON dp.major_id=o.object_id
WHERE dp.grantee_principal_id=USER_ID('pol_admin')
  AND SCHEMA_NAME(o.schema_id)='VCentralPay'
GROUP BY o.name ORDER BY o.name;"

# เช็ค principal มีสิทธิ์ INSERT บนตารางหนึ่งไหม:
... -Q "EXECUTE AS USER='pol_admin';
        SELECT HAS_PERMS_BY_NAME('VCentralPay.ProducerAccounts','OBJECT','INSERT');
        REVERT;"

# unique index (เงื่อนไข dedup registration) = UNIQUE บน ProducerAccounts.Subject
# (person details + name/photo อยู่บนตารางนี้ด้วยแล้ว หลัง AddProducerAccountDetailsDropProfile):
... -Q "SELECT i.name, i.is_unique FROM sys.indexes i
        JOIN sys.tables t ON i.object_id=t.object_id
        WHERE SCHEMA_NAME(t.schema_id)='VCentralPay' AND t.name='ProducerAccounts';"
```

> dev DB ใช้ port `11433`, integration `11434`. ระวังอย่าสลับ.

---

## 9. อ้างอิง

- `docs/reference/producer-module.md` — สเปก producer SSO module เต็ม
- `docs/reference/admin-module.md` — admin SSO + FE integration
- `.ai/shared/ARCHITECTURE.md` — folder layout, principal model
- `.ai/shared/CODING_STANDARDS.md` — version pin, hard constraints
- `docker-compose.yml` / `docker/bootstrap/01-principals.sql` — dev DB + principals
