# คู่มือรันระบบ Local Development

คู่มือนี้ครอบคลุมการตั้งค่าเครื่องครั้งแรก, SQL Server, migration, HTTPS, API, SPA, Google/Microsoft OIDC,
การทดสอบ และการแก้ปัญหาที่พบบ่อย. รันคำสั่งจาก root ของ repository เท่านั้น.

## 1. ผลลัพธ์ที่ต้องได้

| ส่วนประกอบ | URL หรือ port | สถานะที่คาดหวัง |
|---|---:|---|
| Core SQL Server | `localhost:11433` | `pol-db` healthy, database `VCentralPay` |
| Motor simulation | `localhost:11434` | `pol-hippo-db` healthy, database `hippodb` |
| Non-Motor simulation | `localhost:11435` | `pol-mammoth-db` healthy, database `mammothdb` |
| Seq | `http://localhost:5341` | healthy, รับ log เฉพาะเครื่อง local |
| API | `https://localhost:5001` | `/health/live` และ `/health/ready` ตอบสำเร็จ |
| Customer SPA | `https://localhost:3000` | proxy `/api` ไป API port `5001` |
| Admin SPA | `https://localhost:3001` | proxy Admin API ไป API port `5001` |
| Merchant SPA | `https://localhost:3002` | proxy Merchant API ไป API port `5001` |

`5100` เป็น published port ของ production compose เท่านั้น. Local development ใช้ `5001`; ห้ามนำ
`5120`, `5200` หรือ `5300` กลับมาใช้เป็น callback หรือ SPA origin.

## 2. เครื่องมือที่ต้องมี

| เครื่องมือ | เวอร์ชันหรือเงื่อนไข | ตรวจด้วย |
|---|---|---|
| .NET SDK | `10.x` | `dotnet --version` |
| Entity Framework CLI | `10.0.8` | `dotnet ef --version` |
| Docker Desktop และ Compose | รุ่นที่รองรับ Compose v2 | `docker compose version` |
| Git | รุ่นปัจจุบัน | `git --version` |
| `sqlcmd` | ไม่บังคับ; ใช้เมื่อต้องตรวจ DB แบบ live จาก host | `sqlcmd -?` |

ถ้ายังไม่มี EF CLI:

```bash
dotnet tool install --global dotnet-ef --version 10.0.8
```

## 3. ตั้งค่าเครื่องครั้งแรก

### 3.1 เปิด Git hooks และสร้าง local environment

```bash
git config core.hooksPath .githooks
test -f .env || cp .env.example .env
```

แก้ `.env` ด้วยค่าของเครื่อง local. ไฟล์นี้ถูก ignore โดย Git แต่ยังเป็นไฟล์ที่มี secret บนเครื่อง จึงต้องจำกัด
สิทธิ์ผู้ใช้และห้ามส่งผ่าน chat, issue, PR, screenshot หรือ log.

ค่าขั้นต่ำที่ต้องเปลี่ยนจาก placeholder:

| กลุ่ม | คีย์ |
|---|---|
| SQL administrator | `MSSQL_SA_PASSWORD`, `POL_SA_PASSWORD` |
| Runtime DB | `POL_APP_PASSWORD`, `ConnectionStrings__App` |
| EF migrator | `ConnectionStrings__Migrator`, `POL_DESIGN_SQL` |
| Motor simulation | `POL_HIPPO_APP_PASSWORD`, `SpDocument__MotorConnectionString` |
| Non-Motor simulation | `POL_MAMMOTH_APP_PASSWORD`, `SpDocument__NonMotorConnectionString` |
| Local vault | `Vault__MasterKeyBase64` |

ถ้ารัน SPA ให้ uncomment หรือตั้งค่า local origins ต่อไปนี้ใน `.env`:

```text
AdminSession__WebAppBaseUrl=https://localhost:3001
AdminSession__ReturnUrlAllowlist__0=/
AdminSession__ReturnUrlAllowlist__1=/dashboard
AdminSession__ScalarBaseUrl=https://localhost:5001
MerchantSession__WebAppBaseUrl=https://localhost:3002
MerchantSession__ReturnUrlAllowlist__0=/
MerchantSession__ReturnUrlAllowlist__1=/dashboard
Cors__MerchantOrigins__0=https://localhost:3002
Cors__AdminOrigins__0=https://localhost:3001
Psp__PublicBaseUrl=https://localhost:5001
Psp__TwoCTwoP__FrontendReturnUrl=https://localhost:3000/checkout/return
Psp__Omise__ReturnUri=https://localhost:3000/checkout/return
```

อีกทางคือ copy `src/Hosts/Api/appsettings.Development.json.example` เป็นไฟล์
`src/Hosts/Api/appsettings.Development.json` ที่ถูก Git ignore แล้วแทน local password. ตั้งคีย์หนึ่งจุดเท่านั้น เพราะ
environment จาก `.env` มี precedence สูงกว่า Development JSON.

ข้อกำหนดสำคัญ:

- `MSSQL_SA_PASSWORD` และ `POL_SA_PASSWORD` ต้องเป็นค่าเดียวกัน.
- `pol_app`, `hippo_app` และ `mammoth_app` ต้องใช้รหัสต่างกัน.
- รหัสต้องผ่าน SQL Server password policy และไม่ควรมีชื่อ login อยู่ในรหัส.
- Connection string ที่มี `;`, ช่องว่าง หรือ `$` ต้องอยู่ใน single quote ตาม `.env.example`.
- `ConnectionStrings__App` ใช้ `pol_app`; `POL_DESIGN_SQL` และ `ConnectionStrings__Migrator` ใช้ `sa` เฉพาะ local.
- ห้ามใช้ค่า `REPLACE_WITH_*` ต่อ เพราะจะทำให้ bootstrap, DB connection หรือ OIDC ล้มเหลวภายหลัง.

สร้าง local vault key ใหม่ได้ด้วย:

```bash
openssl rand -base64 32
```

นำผลลัพธ์ไปใส่ `Vault__MasterKeyBase64` ใน `.env`; ห้าม commit หรือแสดงค่าผ่าน command output ที่บันทึก log.

### 3.2 ติดตั้งและเชื่อถือ HTTPS certificate

```bash
dotnet dev-certs https --trust
```

macOS จะแสดงหน้าต่างยืนยัน Keychain. ถ้า certificate เดิมเสียจริง ให้รัน `dotnet dev-certs https --clean` แล้ว
`dotnet dev-certs https --trust` ใหม่; `--clean` ลบ development certificate ของ .NET ทุกโปรเจกต์บนเครื่อง จึงไม่ใช้
เป็นขั้นตอนปกติ. หลังสำเร็จให้ปิดและเปิด browser ใหม่ถ้า browser ยังแจ้ง certificate error.

## 4. เริ่ม dependency

```bash
docker compose up -d
docker compose ps
docker compose ps -a pol-db-init
docker compose logs --no-color pol-db-init
```

รอให้ SQL ทั้งสาม instance และ Seq เป็น `healthy`. `pol-db-init` เป็น one-shot service; สถานะที่ถูกต้องคือ
`Exited (0)`. ถ้า `docker compose up -d` คืน prompt แล้ว DB ยังไม่ healthy ให้รอประมาณ 30-60 วินาทีแล้วตรวจซ้ำ.

ถ้า `pol-db-init` ไม่จบด้วย exit code `0`:

1. อ่าน error แรกจาก `docker compose logs --no-color pol-db-init`.
2. ตรวจว่า password ทั้งสี่ชุดไม่เป็น placeholder และตรงกับ connection string.
3. ตรวจ port ด้วย `lsof -nP -iTCP:11433 -sTCP:LISTEN` และทำซ้ำสำหรับ `11434`, `11435`.
4. แก้ config แล้วรัน `docker compose up -d` ซ้ำ; bootstrap เป็น idempotent.

อย่าใช้ `docker compose down -v` เพื่อแก้ปัญหาทั่วไป. คำสั่งนี้ลบ local DB volume และข้อมูลทั้งหมด. ใช้ได้เฉพาะ
เมื่อยืนยันว่าต้องการสร้าง fresh local database และสำรองข้อมูลที่ต้องเก็บแล้ว.

## 5. ใช้ environment ใน shell

API และ `dotnet ef` ไม่อ่าน `.env` อัตโนมัติ. ต้อง export ใน terminal เดียวกับ process ที่จะรัน:

```bash
set -a
source .env
set +a
```

ตรวจเฉพาะชื่อคีย์โดยไม่พิมพ์ค่า secret:

```bash
test -n "${ConnectionStrings__App:-}" && echo "ConnectionStrings__App: set"
test -n "${POL_DESIGN_SQL:-}" && echo "POL_DESIGN_SQL: set"
test -n "${Vault__MasterKeyBase64:-}" && echo "Vault__MasterKeyBase64: set"
```

Environment ไม่ข้าม terminal. เปิด shell ใหม่ต้อง `source .env` ใหม่.

## 6. Apply migration

ใช้ `VCentralPay` ที่ถูกต้องและสำรอง local data ที่ต้องเก็บก่อน. Fresh baseline จะปฏิเสธ target ที่มี application
object หรือ migration history ที่ไม่ตรงก่อนเริ่ม DDL.

```bash
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api

dotnet ef migrations list --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api
```

ปัจจุบันต้องมี 20 migrations และตัวสุดท้ายต้องเป็น:

```text
20260819145219_WorkforceTenantBinding
```

ตรวจ static migration guard โดยไม่ต้องมี `sqlcmd` บน host:

```bash
env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh
```

ถ้ามี `sqlcmd` บน host และต้องการตรวจ schema/seed/grant ของ DB จริง:

```bash
bash docker/bootstrap/assert-fresh-db.test.sh
```

ผลลัพธ์สุดท้ายต้องเป็น `assert-fresh-db.test: OK`. API จะ auto-migrate ใน Development เมื่อ
`ConnectionStrings__Migrator` มีค่า; production ไม่ auto-migrate และต้องใช้ขั้นตอนใน
[Self-host Deployment Runbook](deploy-self-host.md).

## 7. ตั้งค่า Microsoft Entra OIDC

ระบบใช้ server-side OIDC BFF แบบ confidential client. Browser ไม่ควรถือ client secret, authorization code,
ID token หรือ session token. `Client ID`, `Tenant ID` และ Authority เป็น public identifiers; `ClientSecret` เป็น secret.

### 7.1 Configuration matrix

| Tier | ผู้ใช้ | Configuration prefix | Authority | Login path | Callback path | SPA |
|---|---|---|---|---|---|---|
| Tier 0 | พนักงาน/Admin | `AdminAuth__Providers__Microsoft__` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | `/api/v1/admins/auth/microsoft/login` | `/api/v1/admins/auth/microsoft/callback` | `https://localhost:3001` |
| Tier 1 | ตัวแทน/Merchant | `MerchantAuth__Providers__Microsoft__` | `https://<tenant>.ciamlogin.com/<tenant-id>/v2.0` | `/api/v1/merchants/auth/microsoft/login` | `/api/v1/merchants/auth/microsoft/callback` | `https://localhost:3002` |

Authority ต้อง pin tenant เดียวและลงท้าย `/v2.0`. ห้ามใช้ `/common`, `/organizations` หรือ `/consumers`.
Current local launch profile มี public Tier 1 Authority, Client ID และ callback ของ `VCP External DEV` แล้ว แต่ไม่มี
client secret ตามหลัก security.

### 7.2 Inject secret โดยไม่เขียนลง tracked file

วิธีเร็วสำหรับ shell ปัจจุบัน:

```bash
printf 'Merchant Entra client secret Value: '
IFS= read -r -s MerchantAuth__Providers__Microsoft__ClientSecret
printf '\n'
export MerchantAuth__Providers__Microsoft__ClientSecret
```

Tier 0 ต้องตั้ง public values และ secret ของ application คนละตัว:

```bash
export AdminAuth__Providers__Microsoft__ClientId='<admin-application-client-id>'
export AdminAuth__Providers__Microsoft__Authority='https://login.microsoftonline.com/<tenant-id>/v2.0'
export AdminAuth__Providers__Microsoft__CallbackPath='/api/v1/admins/auth/microsoft/callback'

printf 'Admin Entra client secret Value: '
IFS= read -r -s AdminAuth__Providers__Microsoft__ClientSecret
printf '\n'
export AdminAuth__Providers__Microsoft__ClientSecret
```

ใช้ client secret `Value`, ไม่ใช่ `Secret ID`. ถ้า secret เคยปรากฏใน chat, terminal transcript, screenshot, log หรือ
tracked file ให้ revoke แล้วสร้างใหม่ก่อนทดสอบ. หลังหยุด API ให้ล้างค่าจาก shell:

```bash
unset MerchantAuth__Providers__Microsoft__ClientSecret
unset AdminAuth__Providers__Microsoft__ClientSecret
```

### 7.3 ตั้งค่า Microsoft ใน Azure Portal

สำหรับแต่ละ application:

1. เปิด `App registrations` แล้วเลือก application ที่ตรงกับ Tier.
2. ที่ `Authentication` เพิ่ม platform ชนิด `Web`.
3. ใส่ redirect URI แบบ exact match ทั้ง scheme, host, port, path และตัวพิมพ์.
4. Tier 0 ใช้ `https://localhost:5001/api/v1/admins/auth/microsoft/callback`.
5. Tier 1 ใช้ `https://localhost:5001/api/v1/merchants/auth/microsoft/callback`.
6. ที่ `Certificates & secrets` สร้าง secret ใหม่และเก็บเฉพาะ `Value` ใน secret store.
7. Tier 1 ต้องมี sign-up/sign-in user flow, เปิด Email one-time passcode และ link application เข้ากับ user flow.
8. Tier 1 ต้องส่ง `oid` และ claim `email` หรือ `preferred_username` ที่มีรูปแบบอีเมล; ถ้าตั้ง
   `AllowedTenants` ต้องมี `tid` ด้วย.

`Run user flow` ใน Portal พิสูจน์ tenant UX เท่านั้น. การทดสอบ backend end-to-end ต้องเริ่มจาก application login path
เพื่อให้ browser มี state, nonce และ correlation cookie ที่ backend สร้างไว้.

### 7.4 ตั้งค่า Google OIDC สำหรับ Merchant

Google ยังใช้ได้เฉพาะ Merchant user. Admin Google login/callback ไม่ register และต้องตอบ `404`.

| Configuration prefix | Login path | Google Web redirect URI |
|---|---|---|
| `MerchantAuth__Providers__Google__` | `/api/v1/merchants/auth/google/login` | `https://localhost:5001/api/v1/merchants/auth/google/callback` |

Google callback ต้องมี verified `email`. ถ้าตั้ง `HostedDomain`, callback จะรับเฉพาะ claim `hd` ที่ตรงกัน. Merchant
invitation start ใช้ provider-verified email เพื่อ bind invitation อย่างปลอดภัย.

## 8. รัน API

ใน terminal ที่ source `.env` และ inject OIDC secret แล้ว:

```bash
dotnet watch --project src/Hosts/Api/Api.csproj run
```

ตรวจว่าไม่มี API ตัวเก่าถือ port ก่อน restart:

```bash
lsof -nP -iTCP:5001 -sTCP:LISTEN
```

ตรวจ service:

```bash
curl --fail --silent --show-error https://localhost:5001/health/live
curl --fail --silent --show-error https://localhost:5001/health/ready
curl --fail --silent --show-error https://localhost:5001/openapi/v1.json > /dev/null
```

Development endpoints:

- OpenAPI: `https://localhost:5001/openapi/v1.json`
- Scalar: `https://localhost:5001/scalar`
- Liveness: `https://localhost:5001/health/live`
- Readiness: `https://localhost:5001/health/ready`

การเปลี่ยน `appsettings.*`, environment หรือ DI ต้องหยุด process แล้วเริ่มใหม่. Hot reload ไม่รับประกันการโหลด config
และ OIDC scheme ใหม่.

### ย้ายชื่อ session และ CORS config

Compatibility aliases ด้านล่างรองรับชั่วคราวหนึ่ง tagged release. ย้ายทุก environment ให้เสร็จ แล้ว restart API.
หากตั้งชื่อเก่าและใหม่พร้อมกัน ค่า normalized ต้องเท่ากัน; ถ้าต่างกัน startup จะหยุดเพื่อกัน silent fallback.

| ชื่อเก่า | ชื่อใหม่ |
|---|---|
| `AdminSession__SpaBaseUrl` | `AdminSession__WebAppBaseUrl` |
| `MerchantUser__Session__*` | `MerchantSession__*` |
| `MerchantUser__Session__SpaBaseUrl` | `MerchantSession__WebAppBaseUrl` |
| `Cors__AllowedOrigins__*` | `Cors__MerchantOrigins__*` |

API ไม่อ่าน `.env` เอง. หลังแก้ไฟล์ ให้ export เข้า process แล้วเริ่ม API ใหม่:

```bash
set -a && source .env && set +a
dotnet run --project src/Hosts/Api --launch-profile https
```

## 9. รัน SPA

Frontend อยู่คนละ repository. ใช้คำสั่งของแต่ละ frontend แต่ต้องรักษา contract นี้:

| SPA | HTTPS origin | API proxy | CORS entry |
|---|---|---|---|
| Customer | `https://localhost:3000` | `/api` ไป `https://localhost:5001` | ไม่ใช้ console-cookie CORS |
| Admin | `https://localhost:3001` | Admin routes ไป `https://localhost:5001` | `Cors__AdminOrigins__0` |
| Merchant | `https://localhost:3002` | Merchant routes ไป `https://localhost:5001` | `Cors__MerchantOrigins__0` |

OIDC callback ลงที่ API origin `5001`; backend จึง redirect ผลลัพธ์ต่อไปยัง SPA origin ที่กำหนดใน
`AdminSession__WebAppBaseUrl` หรือ `MerchantSession__WebAppBaseUrl`.

## 10. ทดสอบ Microsoft login จริง

ห้ามเปิด callback URL โดยตรง และห้ามนำ authorize/callback URL เก่ามาใช้ซ้ำ. Authorization code และ state ใช้ครั้งเดียว.

### 10.1 Tier 1: Merchant

1. เปิด `https://localhost:5001/api/v1/merchants/auth/microsoft/login`.
2. กรอกอีเมลและ OTP ใน browser เดิม.
3. ยอมรับ consent ถ้า tenant แสดงในครั้งแรก.
4. Entra ส่ง `POST` กลับ callback ด้วย `response_mode=form_post`.
5. ผู้ใช้ใหม่ต้องถูก redirect ไป `https://localhost:3002/register?ticket=<redacted>`.
6. SPA ส่ง registration form ไป `POST /api/v1/merchants/users/register` พร้อม ticket.
7. ผลสมัครสำเร็จต้องเป็น `201` และสถานะ `PendingApproval`.
8. Admin อนุมัติด้วย internal `merchantUserId`, ไม่ใช่ Entra `oid` หรือ `Subject`.
9. Login ซ้ำหลังอนุมัติต้องไป Merchant dashboard และมี server-side session cookie.

สถานะ callback ที่ถูกต้อง:

| สถานะ identity | ผลลัพธ์ |
|---|---|
| ไม่พบในระบบ | `/register?ticket=...` และยังไม่มี session |
| `Rejected` | `/register?ticket=...` สำหรับ correction |
| `PendingApproval` | `/login-error?reason=awaiting-approval` และยังไม่มี session |
| `Suspended` | `/login-error?reason=suspended` |
| `Active` | allowlisted return path เช่น `/dashboard` พร้อม session |

Microsoft invitation start ยังไม่รองรับเพราะ Entra email เป็น mutable/unverified claim. Endpoint invitation รับ Google
เท่านั้น; Microsoft self-service ต้องเริ่มจาก login path แล้วเข้าสู่ registration flow.

### 10.2 Tier 0: Admin

1. เปิด `https://localhost:5001/api/v1/admins/auth/microsoft/login?returnTo=/dashboard`.
2. ใช้ employee account จาก workforce tenant ที่ pin ไว้.
3. Identity ใหม่ต้องผ่าน tenant, `vcp.employee` และ exact `viriyah.co.th` gate; ระบบสร้าง `Active + Scoped` แบบไม่มี role.
4. Login สำเร็จต้อง redirect ไป `https://localhost:3001/dashboard` พร้อม admin session cookie.
5. ก่อน Production ต้อง promote corporate Super ผ่าน admin management API; ไม่มี Microsoft bootstrap allowlist.

## 11. Troubleshooting OIDC

| อาการ | ความหมายหรือจุดตรวจจากหลักฐาน | วิธีแก้ |
|---|---|---|
| Login path ตอบ `404` | provider ไม่รู้จักหรือ `ClientId` ว่างจน scheme ไม่ถูก register | ตั้ง `ClientId`, Authority, callback, secret แล้ว restart API |
| `AADSTS50011` | `redirect_uri` ใน request ไม่ exact match กับ Web redirect URI | คัด URI จาก error แล้วแก้ Portal ให้ตรง scheme, host, port และ path |
| `AADSTS7000215` | token endpoint ปฏิเสธ client secret | ใช้ secret `Value`, ไม่ใช่ `Secret ID`; rotate ถ้าเคยเปิดเผย แล้ว restart API |
| `This account does not exist in this organization` | account ยังไม่อยู่ใน external tenant หรือ user flow ไม่เปิด sign-up | link app กับ sign-up/sign-in user flow แล้วใช้ `Create one`/OTP ตาม policy |
| `.AspNetCore.Correlation.* cookie not found` | callback ไม่ได้อยู่ใน flow เดียวกับ login หรือ cookie/state เก่าหาย | ปิด callback เก่า เริ่มใหม่จาก `/login` ใน browser เดิม และอย่าใช้ Portal callback แทน app flow |
| ไป `/login-error?reason=auth-failed` | OIDC middleware ปฏิเสธ code exchange, issuer, audience, nonce หรือ protocol | ดู error แรกใน API log; ห้ามสรุปจาก generic redirect อย่างเดียว |
| ไป `/login-error?reason=awaiting-approval` | authentication ผ่านแล้ว แต่ registration ยัง `PendingApproval` | ให้ Admin อนุมัติ internal `merchantUserId` แล้ว login ใหม่ |
| ไป `/register?ticket=...` | authentication ผ่านแล้วและยังไม่มี local identity | ถือเป็นผลสำเร็จของ callback; เปิด Merchant SPA และกรอก registration ต่อ |
| redirect กลับ port เก่า | process ยังอ่าน config เดิมหรือ SPA base URL ถูก override | ตรวจ environment, หยุด API ทุกตัว แล้ว start ใหม่; ใช้ `5001/3001/3002` |

เก็บหลักฐานด้วย `RequestId`, `TraceId`, error code และ timestamp. ห้ามแนบ authorization code, state, cookie,
registration ticket, OTP, ID token หรือ client secret.

## 12. รันทดสอบ

Build และ non-integration suite:

```bash
dotnet build pol-core.slnx -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
```

Integration suite ต้องใช้ local SQL ที่แยกจาก shared/prod data และ source environment เดียวกัน:

```bash
source .env.integration
dotnet test tests/Integration.Tests/Integration.Tests.csproj \
  --filter "Category=Integration"
```

OIDC/migration regression ที่เกี่ยวข้องโดยตรง:

```bash
dotnet test tests/Hosts.Tests/Hosts.Tests.csproj \
  --filter "FullyQualifiedName~MicrosoftAuthLoginRedirectTests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~MerchantUserAuthLoginRedirectTests"

source .env.integration
dotnet test tests/Integration.Tests/Integration.Tests.csproj \
  --filter "FullyQualifiedName~ProviderDiscriminatorMigrationTests"
```

Repository gates:

```bash
env -u POL_SA_PASSWORD bash docker/bootstrap/assert-fresh-db.test.sh
.ai/bin/check-secrets.sh --all
```

Automated OIDC tests ใช้ fake backchannel เพื่อพิสูจน์ state, nonce, PKCE, issuer, audience, callback และ branching.
Live browser test ยังจำเป็นเพื่อพิสูจน์ Entra tenant, redirect registration, client secret และ user flow จริง.

## 13. Commerce smoke test

1. Login Merchant user ที่มีสถานะ `Active`.
2. Query Product ด้วย SaleCode ที่ bind แล้ว.
3. สร้าง Cart และเพิ่ม `productCode`, `variantCode`, quantity.
4. ส่ง `POST /api/v1/orders`.
5. ยืนยัน `201`, `Location`, Order `Pending` และ Cart `CheckedOut`.
6. สร้าง payment session และ redirect ไป PSP sandbox.
7. ส่งหรือรับ webhook ยืนยัน แล้วตรวจ Order เป็น `Paid` เพียงครั้งเดียว.
8. Replay request เดิมเพื่อตรวจ idempotency.
9. ยืนยัน Checkout/policy routes ที่ retire แล้วตอบ `404`.

Frontend mapping อยู่ที่ `.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`.

## 14. หยุดระบบ

หยุด API ด้วย `Ctrl+C`. ล้าง OIDC secret จาก shell แล้วหยุด container โดยไม่ลบ volume:

```bash
unset MerchantAuth__Providers__Microsoft__ClientSecret
unset AdminAuth__Providers__Microsoft__ClientSecret
docker compose stop
```

ใช้ `docker compose down` เมื่อต้องการลบ container/network แต่เก็บ named volume. ห้ามเติม `-v` เว้นแต่ยืนยันว่าจะ
ลบ local database และ Seq data ทั้งหมด.
