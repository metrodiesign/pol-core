# Admin Microsoft OIDC และ Tier 0 employee profile

คู่มือนี้อธิบาย Admin Microsoft runtime หลัง tenant-aware cutover และ optional employee-profile enrichment
ขั้นตอน schema/offline mapping โดยละเอียดอยู่ที่
[Tenant-aware identity cutover](admin-workforce-jit-rollout.md)

## 1. Runtime identity contract

Microsoft Admin ใช้ immutable identity เดียว:

```text
Provider = "microsoft"
TenantId = validated tid
Subject  = canonical validated oid
```

- `tid` และ `oid` ต้องมีอย่างละหนึ่งค่าพอดี เป็น non-empty GUID และ `tid` ต้องตรง tenant ที่ derive จาก
  tenant-pinned Authority
- signature, issuer, audience, nonce, lifetime, state และ authorization-code exchange ต้องผ่าน framework ก่อนอ่าน
  workforce claims, เรียก Graph, query database หรือสร้าง session
- lookup, JIT, conflict และ recovery ใช้ exact `(Provider, TenantId, Subject)` เท่านั้น
- Email เป็น optional non-unique contact attribute อาจ absent, mutable, reused หรือซ้ำกันได้
- Admin Microsoft ไม่ fallback ไป Email, UPN, `preferred_username`, `WorkforceEmailKey` หรือ `EmployeeId`
- claims ไม่เปลี่ยน Tier, role, permission หรือ `MerchantAccess`
- unknown exact tuple ทำ roleless `Active + Scoped` JIT; Suspended exact tuple ถูกปฏิเสธ
- session ownership ยังคง internal `AdminId`

Authority ต้องมีรูปแบบ:

```text
https://login.microsoftonline.com/<workforce-tenant-id>/v2.0
```

ห้ามใช้ `/common`, `/organizations` หรือ `/consumers` Admin Google login/callback ไม่ register Merchant
Google/Microsoft ใช้ configuration, scheme, cookie และ behavior ของ Merchant แยกต่างหาก

## 2. Configuration

| หัวข้อ | ค่า |
|---|---|
| Admin provider prefix | `AdminAuth:Providers:Microsoft` |
| callback | `/api/v1/admins/auth/microsoft/callback` |
| Graph base URL | `AdminAuth:GraphBaseUrl`; Production pin `https://graph.microsoft.com` |
| Graph permission | delegated `User.Read`; mandatory |
| authorization scopes | `openid email profile User.Read` |
| profile migration | `20260830172117_Tier0EmployeeProfile` |
| identity migration | `20260902133906_Tier0MicrosoftTenantAwareIdentity` |

`email` scope เป็น best-effort contact เท่านั้น Login แบบไม่มี email ต้องสำเร็จได้เมื่อ exact tuple valid

Production boot ต้อง fail เมื่อ Microsoft provider ไม่ครบ, Authority ไม่ pin tenant, callback ผิด หรือ configured tenant
ไม่ตรง persisted `WorkforceTenantBinding` singleton Schema ที่มี triple index ไม่อนุญาตให้ runtime รับ tenant เพิ่ม

## 3. Entra setup

1. เพิ่ม Web redirect URI แบบ exact:
   `https://<api-origin>/api/v1/admins/auth/microsoft/callback`
2. ใช้ confidential client และเก็บ client secret `Value` ใน secret store ห้ามใช้ Secret ID
3. ตั้ง tenant-pinned Authority ตามข้อ 1
4. ใช้ Conditional Access, MFA และ Enterprise Application assignment เป็น access policy ฝั่ง Entra
5. ไม่สร้าง App Role เพื่อ map Tier หรือ permission
6. เพิ่ม delegated `User.Read` และ grant admin consentก่อนเปิด Admin login traffic
7. ตรวจว่า directory object ID จาก authoritative export ตรงกับ token claim `oid`; ห้าม derive จาก Email
8. ตรวจว่า `employeeId` ถูก sync และตรง HR mirrorหลัง normalization

หากไม่มี effective `User.Read`, Graph `401/403`, access tokenหาย หรือ providerคืน exact `consent_required`, loginจบด้วย
`employee-profile-unavailable` ไม่มี User/profile/session success writeและมี generic denied-auth auditเท่านั้น Userยกเลิก
loginด้วย `access_denied`ยังได้ `access-denied` ระบบไม่ parse `error_description`, AADSTSหรือ exception message

## 4. Employee-profile flow

ทุก Admin Microsoft OIDC callbackใหม่ที่ protocolและ workforce validationผ่านใช้ access tokenแบบ transientเพื่อเรียก:

```http
GET /v1.0/me?$select=employeeId
```

access token ไม่ถูก persist จากนั้นระบบ normalize `employeeId` และ query `dbo.VibEmp` ด้วย exact parameterized
`EmpCode` match โดยอ่านเฉพาะ `EmpCode`, `FirstNameTh`, `LastNameTh` แล้ว commit identity, profileและ `UserAudits`
ตาม transaction contract Existing Admin session requestและ session rotationไม่ใช่ OIDC callbackใหม่ จึงไม่เรียก Graph

กฎ profile:

- `EmployeeId` เป็น profile attribute ไม่ใช่ identity key และ unique แบบ global ใน runtime single tenant ปัจจุบัน
- `FirstName` และ `LastName` refreshเมื่อ HR value validและเปลี่ยนจริง
- no-opไม่ bump `Version`, ไม่ stamp `UpdatedAt`และไม่ append profile audit
- name change bump `Version`หนึ่งครั้ง, stamp `UpdatedAt`, append `employee-profile-sync` และไม่ bump `AuthorizationVersion`
- `PositionId`, `OfficeId`, `LevelId`, `DivisionId`, Tier, rolesและ `MerchantAccess`ไม่ถูกอ่านหรือเปลี่ยน
- Graph/HR/mismatch/taken failure rollback JIT/profile/success auditsและไม่สร้าง session

## 5. เตรียม read-only HR source

`dbo.VibEmp` เป็น external/operator-managed table ระบบนี้ไม่สร้าง, alterหรือ seed production table Schemaที่ runtimeอ่านมีเพียง:

```text
EmpCode
FirstNameTh
LastNameTh
```

ถ้าตารางมีอยู่ก่อน migration `20260830172117_Tier0EmployeeProfile` migrationจะ grant `SELECT`แบบ conditional
ถ้าตารางถูกสร้างภายหลัง ให้ privileged operatorรัน idempotent stepนี้ก่อนเปิด Admin login traffic:

```sql
IF OBJECT_ID(N'dbo.VibEmp', N'U') IS NULL
    THROW 51000, N'HR source is not available.', 1;
GRANT SELECT ON dbo.VibEmp TO pol_app;
```

ตรวจ least privilegeโดยไม่อ่าน employee rows:

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

ผลที่ยอมรับคือ `CanSelect=1` และค่าอื่นทุกตัวเป็น `0` ถ้ายังไม่มี grant runtimeคืน
`employee-profile-unavailable`โดยไม่มี SQL detailหรือ parameter valueใน browser/log

บน local dev ตารางกลุ่มนี้ไม่มี migration หรือ `docker/bootstrap` สร้างให้ (REQ-8.7) ดังนั้นทุกครั้งที่
รีเซ็ตหรือ migrate database ใหม่ ให้โหลดจาก dump ของ operator เองด้วย
`./scripts/load-hr-mirror.sh [--tables VibEmp,branch]` ซึ่ง drop แล้วโหลดใหม่และ re-grant `SELECT`
ให้ `pol_app` แบบ idempotent จบด้วยการเทียบจำนวนแถวกับจำนวน `INSERT` ในไฟล์ dump

ค่าเริ่มต้นโหลดครบสี่ตารางใช้เวลาประมาณ 4 นาที เพราะ `dbo.sale` กินพื้นที่เกือบทั้ง dump ตัวที่สอง
(~284MB) และไม่มี consumer ในโค้ด ถ้าต้องการเฉพาะที่ runtime อ่านจริงให้ใช้ `--tables VibEmp,branch`
ซึ่งใช้เวลาประมาณ 20 วินาที Docker VM มี RAM 8GB ต่อ SQL Server สามตัว การโหลดพร้อมงานหนักอื่นทำให้
`pol-db` ถูก OOM kill (exit 137) ได้ ให้รันทีละครั้ง (สคริปต์กัน run ซ้อนด้วย
`/tmp/load-hr-mirror.lock`)

## 6. Pre-bound Microsoft invite

Super Admin สร้าง invite ผ่าน `POST /api/v1/admins` พร้อม CSRF:

```json
{
  "objectId": "<verified-entra-object-guid>",
  "identityApprovalReference": "<non-sensitive-reference>",
  "email": "<optional-contact>"
}
```

- `objectId` ต้องมาจาก verified Entra export ของ persisted tenant
- `identityApprovalReference` ต้อง non-empty, trimmed และไม่เกิน 128 characters; ถูกเก็บเป็น correlation ของ
  `create-scoped` audit
- Email optional และไม่ unique Invalid/blank/overlength contact ถูก normalize เป็น `NULL` โดยไม่ block valid tuple
- account ถูก persist ด้วย final tuple ตั้งแต่สร้าง First login resolve `AdminId` เดิมโดย exact tuple
- ไม่มี Microsoft invite ที่รอ bind ด้วย Email และไม่มี identity-mutation endpoint ภายหลัง

ห้ามใส่ raw approval evidence, `tid`, `oid`, Email หรือ `EmployeeId` ลง audit payload

## 7. Deployment order

1. ผ่าน tenant-aware schema/offline mapping ตาม
   [cutover runbook](admin-workforce-jit-rollout.md) และคง Admin traffic ปิดตลอดช่วง incompatible schema
2. provision `dbo.VibEmp`, ตรวจสาม source columnsและ grant `pol_app`เฉพาะ `SELECT`
3. grant `User.Read` admin consent
4. start new binary ให้ startup tenant/state verifier ผ่าน
5. staging ทดสอบ email-less exact login, JIT, pre-bound invite, profile bind/refreshและ session requestที่ไม่เรียก Graph
6. Production smoke ใช้ approved existing pre-mapped account เท่านั้น ห้ามสร้าง JIT/invite mutation เพื่อ smoke
7. เปิด traffic แล้ว monitor fixed aggregate categories โดยไม่มี identity/profile values

## 8. Failure map

| Browser reason | สาเหตุหลัก | Writes ที่ยอมรับ |
|---|---|---|
| `auth-failed` | protocol, code exchange, signature, audience, nonce หรือ lifetime fail | generic denied-auth audit บน fresh scope |
| `workforce-access-denied` | issuer หรือ exact-one `tid`/`oid` invalid, tenant mismatch | generic denied-auth audit; ไม่เรียก Graph/DB/session |
| `suspended` | exact tuple เป็น Suspended | denied-auth audit; ไม่มี session |
| `identity-conflict` | employee mismatch/taken หรือ unresolved unique race | rollback resolution; denied-auth audit |
| `employee-profile-unavailable` | Graph/HR dependency unavailable | rollback resolution; denied-auth audit |
| `employee-profile-missing` | Graph ไม่มี `employeeId` หรือ HR ไม่มี exact row | rollback resolution; denied-auth audit |
| `employee-profile-invalid` | employeeIdหรือ HR row malformed, ชื่อ invalid หรือ cardinalityมากกว่าหนึ่ง | rollback resolution; denied-auth audit |

Browser query string มีเพียง fixed reason label ไม่มี claim, Email หรือ EmployeeId

## 9. EmployeeId conflict และ data repair

ไม่มี supported endpoint, command หรือ runbook SQL สำหรับ unlink/reassign `EmployeeId` เพราะ bound value เป็น global
profile conflict control การย้าย account หรือแก้ ownership ต้องผ่าน separately approved HR-domain/data-repair process
ที่ระบุ target, authorization, transaction, audit และ session impact ห้ามแก้ conflict ด้วย Email fallback หรือย้าย
Microsoft tuple

ก่อนเปิดหลาย workforce tenant ต้องมี HR-domain review ว่า EmployeeId namespace ยังคง global หรือเปลี่ยนเป็น
`(TenantId, EmployeeId)` การมี tenant-aware identity indexไม่เปลี่ยน EmployeeId policy อัตโนมัติ

## 10. Privacy และ logs

API, migration tool, CI, browser reason และ identity audits ต้องไม่บันทึก:

- authorization code, ID/access token, nonce, state, session token หรือ cookie
- `tid`, `oid`, Email หรือ `EmployeeId`
- Microsoft Graph response body
- manifest path/content/digest, approval evidence หรือ target
- exception object/message ที่อาจมี SQL values

allowed diagnostic คือ fixed category/status class/SQL error number, internal `AdminId` เมื่อจำเป็น และ correlation ID
ที่ non-sensitive Microsoft auth audit subject ต้องเป็น `NULL`

## 11. Rollback

ไม่มี profile switchสำหรับ bypass Graph Rollbackต้องใช้ binaryที่ยังบังคับ employee profileหรือปิด Admin login trafficทั้งก้อน
ห้าม deploy Email-only binary, reconstruct object IDจาก Emailหรือรัน guarded identity migration `Down()`ใน production
ใช้ forward recoveryหรือ verified backup restoreตาม cutover runbook

Existing session ไม่ถูก revoke จาก migration และยังใช้ expiry, rotation, reuse detection และ revocation contract เดิม

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
