# Employee login (Microsoft Entra workforce ผ่าน IdentityAccess) และ Tier 0 identity

คู่มือนี้อธิบาย runtime ของ employee login ที่เป็น credential เดียวของ admin console หลัง 2026-09-14: Microsoft Entra
workforce OIDC ทำที่ API (`IdentityAccess:Workforce`, scheme `IdentityWorkforceMicrosoft`) แล้ว OpenIddict ออก platform JWT
ให้ admin SPA (authorization code + PKCE) legacy admin cookie stack (`AdminAuth:*`, admin session cookie, CSRF double-submit,
ตาราง `admin.Sessions`, Microsoft Graph employee-profile lookup ตอน login, ตาราง `admin.Users` และ route `/api/v1/admins/**`)
ถูก retire ทั้งหมด employee identity เป็น `acct.Accounts` ที่ JIT ตอน login สัญญาฝั่ง SPA ดู
[`docs/reference/admins.md`](../reference/admins.md)

## 1. Runtime identity contract

Employee login ใช้ immutable identity เดียว:

```text
Provider = "microsoft"
TenantId = validated tid
Subject  = canonical validated oid
```

- `tid` และ `oid` ต้องมีอย่างละหนึ่งค่าพอดี เป็น non-empty GUID และ `tid` ต้องตรง tenant ที่ derive จาก
  tenant-pinned Authority (`IdentityAccess:WorkforceTenantId` ต้องเท่ากับ tenant ใน Authority)
- signature, issuer, audience, nonce, lifetime, state และ authorization-code exchange ต้องผ่าน framework ก่อนอ่าน
  workforce claims, query database หรือ sign in `pol_login`
- lookup, JIT, conflict และ recovery ใช้ exact `(Provider, TenantId, Subject)` เท่านั้น
- Email เป็น optional non-unique contact attribute อาจ absent, mutable, reused หรือซ้ำกันได้
- runtime ไม่ fallback ไป Email, UPN, `preferred_username`, `WorkforceEmailKey` หรือ `EmployeeId` เพื่อ
  resolve/bind/JIT/authorization; contact email มาจาก id_token `email` claim เท่านั้น (ไม่มี Graph fallback แล้ว)
- claims ไม่เปลี่ยน Tier, role, permission หรือ `MerchantAccess`
- unknown exact tuple ทำ roleless `Active + Scoped` JIT; Suspended exact tuple ถูกปฏิเสธ
- login ที่สำเร็จไม่สร้าง server-side admin session: callback sign in cookie `pol_login` อายุ 2 นาที (scheme `identity-login`)
  เพื่อให้ `/oauth/authorize` ออก authorization code แล้วทิ้ง cookie ต่อจากนั้น SPA ถือ access JWT (15 นาที) + refresh token
  (480 นาที prod / 1440 dev) และส่ง `Authorization: Bearer` ทุก request ownership ของ login = OpenIddict authorization
  หนึ่งรายการต่อ login ผูกกับ `AccountId`

Authority ต้องมีรูปแบบ:

```text
https://login.microsoftonline.com/<workforce-tenant-id>/v2.0
```

ห้ามใช้ `/common`, `/organizations` หรือ `/consumers` Google ถูก retire ทั้งระบบ: ไม่มี login/callback ของ google ทั้งสอง plane
Merchant Microsoft ใช้ configuration, scheme, cookie และ behavior แยกต่างหาก (ยังเป็น BFF cookie)

## 2. Configuration

| หัวข้อ | ค่า |
|---|---|
| workforce provider section | `IdentityAccess:Workforce` (`ClientId`, `ClientSecret`, `Authority`, `CallbackPath`, `Scopes` default `openid profile email`) |
| tenant pin | `IdentityAccess:WorkforceTenantId` ต้องเท่ากับ tenant GUID ใน Authority; `IdentityAccess:WorkforceIssuer`/`WorkforceAudience` ใช้ตรวจ token |
| callback (Entra redirect URI) | `/api/v1/admins/auth/microsoft/callback` (Production บังคับค่านี้) |
| SPA origin | `IdentityAccess:WorkforceWebAppBaseUrl` (ใช้ทั้ง redirect URI `<origin>/auth/callback` ของ OpenIddict public client และหน้า `/login-error`) |
| OpenIddict public client | `IdentityAccess:WorkforceClientId` default `pol-admin`; API register เองตอน boot |
| token lifetime | `IdentityAccess:AccessTokenMinutes` 15, `IdentityAccess:RefreshTokenMinutes` 480 (dev override 1440) |
| issuer | `OAuth:Issuer` ต้องเป็น public origin ของ API (dev `https://localhost:5001`) |
| identity migration | `20260902133906_Tier0MicrosoftTenantAwareIdentity` |
| session retire migration | `20260914051532_RetireAdminSessions` (drop `admin.Sessions`; `admin.AuthAudits` เหลือเป็น archive) |

ไม่มี Graph base URL, ไม่มี `User.Read` และไม่มี `AdminAuth:*` section อีกต่อไป `email` scope เป็น best-effort contact เท่านั้น
Login แบบไม่มี email ต้องสำเร็จได้เมื่อ exact tuple valid

Production boot ต้อง fail (`ProvisioningGuards.RequireWorkforceAdminProvider`) เมื่อ `IdentityAccess:Workforce:ClientId`/`ClientSecret`
ว่างหรือเป็น placeholder, `CallbackPath` ไม่ใช่ค่าบังคับ, Authority ไม่ใช่ public-cloud tenant-pinned `/v2.0` หรือ
`IdentityAccess:WorkforceTenantId` ไม่ตรง tenant ใน Authority และเมื่อ configured tenant ไม่ตรง persisted
`WorkforceTenantBinding` singleton Schema ที่มี triple index ไม่อนุญาตให้ runtime รับ tenant เพิ่ม

prod compose ป้อนค่าเหล่านี้จาก `ADMIN_ENTRA_CLIENT_ID`, `ADMIN_ENTRA_AUTHORITY`, `ADMIN_ENTRA_TENANT_ID`, secret file
`admin_entra_client_secret` (entrypoint export เป็น `IdentityAccess__Workforce__ClientSecret`) และ `ADMIN_FRONTEND_ORIGIN`
ดู [deploy-self-host.md](deploy-self-host.md) ส่วน local dev ดู [local-dev-run.md](local-dev-run.md) ข้อ 7

## 3. Entra setup

1. เพิ่ม Web redirect URI แบบ exact: `https://<api-origin>/api/v1/admins/auth/microsoft/callback`
   (redirect URI ของ SPA `<admin-origin>/auth/callback` เป็นของ OpenIddict public client ที่ API register เองตอน boot
   ไม่ต้องใส่ใน Entra)
2. ใช้ confidential client และเก็บ client secret `Value` ใน secret store ห้ามใช้ Secret ID
3. ตั้ง tenant-pinned Authority ตามข้อ 1 และตั้ง `IdentityAccess:WorkforceTenantId` ให้ตรง
4. ใช้ Conditional Access, MFA และ Enterprise Application assignment เป็น access policy ฝั่ง Entra
5. ไม่สร้าง App Role เพื่อ map Tier หรือ permission
6. ไม่ต้องขอ delegated Graph permission หรือ admin consent: scope ที่ใช้มีเพียง `openid profile email`
7. ตรวจว่า directory object ID จาก authoritative export ตรงกับ token claim `oid`; ห้าม derive จาก Email

User ยกเลิก login ด้วย `access_denied` ได้ `access-denied`; remote/protocol failure ได้ `auth-failed` ระบบไม่ parse
`error_description`, AADSTS หรือ exception message ลง browser reason

## 4. Employee profile ตอน login (retire แล้ว)

Microsoft Graph lookup (`GET /v1.0/me?$select=employeeId,...`) และการ bind `EmployeeId`/ชื่อจาก `dbo.VibEmp` ตอน OIDC callback
ถูก retire พร้อม legacy admin cookie stack callback ปัจจุบันทำเพียง validate claims, resolve/JIT exact tuple แล้ว sign in
`pol_login` ไม่เรียก Graph ไม่ query HR mirror และไม่ persist access token ของ Microsoft

`EmployeeProfileReader` (`dbo.VibEmp`, อ่านเฉพาะ `EmpCode`, `FirstNameTh`, `LastNameTh`) ยังอยู่ใน codebase และถูก register ใน DI
แต่ไม่มี caller ตอน login (ณ 2026-09-14 ไม่มี caller อื่นเช่นกัน) กฎ profile ที่ยังมีผลกับ record เดิม:

- `EmployeeId` เป็น profile attribute ไม่ใช่ identity key; ค่าที่ bind ไว้แล้วยัง unique แบบ global
- Tier, roles และ `MerchantAccess` ไม่ถูกอ่านหรือเปลี่ยนจาก claims

## 5. เตรียม read-only HR source (ไม่บังคับสำหรับ login แล้ว)

`dbo.VibEmp` เป็น external/operator-managed table ระบบนี้ไม่สร้าง, alter หรือ seed production table Schema ที่ runtime อ่านมีเพียง:

```text
EmpCode
FirstNameTh
LastNameTh
```

ถ้าตารางมีอยู่ก่อน migration `20260830172117_Tier0EmployeeProfile` migration จะ grant `SELECT` แบบ conditional
ถ้าตารางถูกสร้างภายหลัง ให้ privileged operator รัน idempotent step นี้:

```sql
IF OBJECT_ID(N'dbo.VibEmp', N'U') IS NULL
    THROW 51000, N'HR source is not available.', 1;
GRANT SELECT ON dbo.VibEmp TO pol_app;
```

ตรวจ least privilege โดยไม่อ่าน employee rows:

```sql
EXECUTE AS USER = 'pol_app';
SELECT
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'SELECT') AS CanSelect,
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'INSERT') AS CanInsert,
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'UPDATE') AS CanUpdate,
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'DELETE') AS CanDelete,
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'ALTER') AS CanAlter,
    HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'CONTROL') AS CanControl;
REVERT;
```

ผลที่ยอมรับคือ `CanSelect=1` และค่าอื่นทุกตัวเป็น `0` การไม่มี grant ไม่กระทบ employee login อีกต่อไป

บน local dev ตารางกลุ่มนี้ไม่มี migration หรือ `docker/bootstrap` สร้างให้ (REQ-8.7) ถ้าต้องใช้ ให้โหลดจาก dump ของ operator
เองด้วย `./scripts/load-hr-mirror.sh [--tables VibEmp,branch]` ซึ่ง drop แล้วโหลดใหม่และ re-grant `SELECT` ให้ `pol_app`
แบบ idempotent (ประมาณ 20 วินาทีเมื่อระบุ `--tables VibEmp,branch`; โหลดครบสี่ตารางประมาณ 4 นาที และห้ามรันซ้อน เพราะ Docker VM
8GB ต่อ SQL Server สามตัวอาจทำให้ `pol-db` ถูก OOM kill exit 137; สคริปต์กัน run ซ้อนด้วย `/tmp/load-hr-mirror.lock`)

## 6. Pre-bound Microsoft invite

Super Admin สร้าง invite ผ่าน `POST /api/v1/admins` (Bearer platform token; ไม่มี CSRF):

```json
{
  "objectId": "<verified-entra-object-guid>",
  "identityApprovalReference": "<non-sensitive-reference>",
  "email": "<contact-email>"
}
```

- `objectId` ต้องมาจาก verified Entra export ของ persisted tenant
- `identityApprovalReference` ต้อง non-empty, trimmed และไม่เกิน 128 characters; ถูกเก็บเป็น correlation ของ
  `create-scoped` audit
- Email **บังคับ** ที่ endpoint นี้ (deliverable contact); blank/overlength/invalid ถูก reject เป็น `400` แต่ Email ยัง
  ไม่ unique จึงซ้ำกันได้ (email-optional/`NULL` ใช้กับ JIT login path เท่านั้น)
- account ถูก persist ด้วย final tuple ตั้งแต่สร้าง First login resolve `AdminId` เดิมโดย exact tuple
- ไม่มี Microsoft invite ที่รอ bind ด้วย Email และไม่มี identity-mutation endpoint ภายหลัง

ห้ามใส่ raw approval evidence, `tid`, `oid`, Email หรือ `EmployeeId` ลง audit payload

## 7. Deployment order

1. apply migration ล่าสุด (`docker/migrate-entrypoint.sh`) และคง Admin traffic ปิดตลอดช่วง incompatible schema
2. ตั้ง `IdentityAccess__Workforce*`, `IdentityAccess__WorkforceTenantId` และ `OAUTH_ISSUER` ให้ครบ (ดูข้อ 2)
3. start new binary ให้ boot guard และ startup tenant/state verifier ผ่าน (migration `RetireAdminSessions` drop `admin.Sessions`)
4. staging ทดสอบจาก admin SPA: email-less exact login, JIT, pre-bound invite, code exchange ที่ `POST /oauth/token`, Bearer
   `GET /api/v1/admins/me`, refresh และ `POST /api/v1/auth/logout`
5. Production smoke ใช้ approved existing pre-mapped account เท่านั้น ห้ามสร้าง JIT/invite mutation เพื่อ smoke
6. เปิด traffic แล้ว monitor fixed aggregate categories โดยไม่มี identity values

## 8. Failure map

callback ที่ล้มเหลว redirect ไป `<IdentityAccess:WorkforceWebAppBaseUrl>/login-error?reason=<label>` (`DenyToWebApp` ใน
`IdentityAccessWiring.cs`):

| Browser reason | สาเหตุหลัก | Writes ที่ยอมรับ |
|---|---|---|
| `auth-failed` | remote failure: protocol, state, code exchange, signature, issuer, audience, nonce หรือ lifetime fail (`OnRemoteFailure`) | ไม่มี identity write; ไม่มี `pol_login` |
| `access-denied` | user ยกเลิกที่ Entra (`OnAccessDenied`) | ไม่มี identity write |
| `<code>` ของ `IdentityAccessException` โดยแทน `_` ด้วย `-` เช่น `workforce-not-eligible`, `account-suspended` | policy/JIT denial ที่ `IdentityBffLoginService.CompleteAsync` (tenant/issuer/audience/eligibility, exact tuple Suspended, account type conflict) | rollback JIT; log warning เฉพาะ code + TraceId |

Browser query string มีเพียง fixed reason label ไม่มี claim, Email หรือ EmployeeId reason จาก Graph/profile
(`employee-profile-*`, `workforce-email-unavailable`) ไม่มีอีกต่อไป

## 9. EmployeeId conflict และ data repair

ไม่มี supported endpoint, command หรือ runbook SQL สำหรับ unlink/reassign `EmployeeId` เพราะ bound value เป็น global
profile conflict control การย้าย account หรือแก้ ownership ต้องผ่าน separately approved HR-domain/data-repair process
ที่ระบุ target, authorization, transaction, audit และ session impact ห้ามแก้ conflict ด้วย Email fallback หรือย้าย
Microsoft tuple

ก่อนเปิดหลาย workforce tenant ต้องมี HR-domain review ว่า EmployeeId namespace ยังคง global หรือเปลี่ยนเป็น
`(TenantId, EmployeeId)` การมี tenant-aware identity index ไม่เปลี่ยน EmployeeId policy อัตโนมัติ

## 10. Privacy และ logs

API, migration tool, CI, browser reason และ identity audits ต้องไม่บันทึก:

- authorization code, Microsoft ID/access token, platform access/refresh token, nonce, state หรือ `pol_login` cookie
- `tid`, `oid`, Email หรือ `EmployeeId`
- manifest path/content/digest, approval evidence หรือ target
- exception object/message ที่อาจมี SQL values

allowed diagnostic คือ fixed category/status class/SQL error number, `IdentityAccessException.Code`, internal `AdminId`/`AccountId`
เมื่อจำเป็น และ correlation ID ที่ non-sensitive

## 11. Rollback

Rollback ต้องใช้ binary ที่ยังบังคับ exact tuple contract หรือปิด Admin login traffic ทั้งก้อน ห้าม deploy Email-only binary,
reconstruct object ID จาก Email หรือรัน guarded identity migration `Down()` ใน production ใช้ forward recovery หรือ verified
backup restore ตาม cutover runbook

`RetireAdminSessions` drop ตาราง `admin.Sessions` ที่ไม่มี reader แล้ว การ rollback binary ไปรุ่นที่ยังใช้ admin cookie ต้อง restore
schema จาก backup เพราะ migration นี้ไม่ recreate ข้อมูล session token ที่ออกไปแล้วยังใช้ได้ตาม lifetime และ revocation ของ
OpenIddict (`POST /api/v1/auth/logout`, `DELETE /api/v1/me/sessions/{sessionId}`, `POST /api/v1/accounts/{accountId}/session-revocations`)

## 12. Gates ก่อน ship

```bash
dotnet build pol-core.slnx --no-restore -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test pol-core.slnx --filter "Category=Integration"
bash docker/migrate-entrypoint.test.sh
bash docker/bootstrap/assert-fresh-db.test.sh
scripts/check-migration-script.sh
.ai/bin/check-secrets.sh --all
scripts/spec-trace.sh admin-employee-profile-sync
```
