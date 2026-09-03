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
| profile switch | `AdminAuth:Providers:Microsoft:RequireEmployeeProfile` หรือ `ADMIN_REQUIRE_EMPLOYEE_PROFILE`; default `false` |
| Graph base URL | `AdminAuth:GraphBaseUrl`; default `https://graph.microsoft.com` |
| Graph permission เมื่อเปิด switch | delegated `User.Read` |
| scopes เมื่อปิด switch | `openid email profile` |
| scopes เมื่อเปิด switch | `openid email profile User.Read` |
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
6. ถ้าเปิด employee profile ให้เพิ่ม delegated `User.Read` และ grant admin consent
7. ตรวจว่า directory object ID จาก authoritative export ตรงกับ token claim `oid`; ห้าม derive จาก Email
8. ถ้าเปิด employee profile ให้ตรวจว่า `employeeId` ถูก sync และตรง HR mirror หลัง normalization

หาก `User.Read` ยังไม่ consent ขณะ switch เปิด Graph จะปฏิเสธ request, login จบด้วย
`employee-profile-unavailable`, ไม่มี User/profile/session success write และมี generic denied-auth audit เท่านั้น

## 4. Employee-profile flow

เมื่อ switch เปิด ระบบใช้ access token แบบ transient ใน validated callback เพื่อเรียก:

```http
GET /v1.0/me?$select=employeeId
```

access token ไม่ถูก persist จากนั้นระบบ normalize `employeeId`, อ่าน HR mirror (`dbo.VibEmp`, `dbo.branch`) และ map
`cfg.Offices.LegacyKey`/`cfg.Divisions.LegacyKey` แล้ว commit User identity/profile/success audits ตาม transaction
contract

เมื่อ switch ปิด:

- ไม่เรียก Graph
- ไม่อ่าน HR
- ไม่แตะ profile
- exact tenant-aware identity/JIT/session flow ยังคงทำงาน ไม่ใช่ legacy Email flow

กฎ profile เดิมยังคงอยู่:

- `EmployeeId` เป็น profile attribute ไม่ใช่ identity key และ unique แบบ global ใน runtime single tenant ปัจจุบัน
- `FirstName`, `LastName`, `OfficeId`, `DivisionId` refresh เมื่อข้อมูล valid และเปลี่ยนจริง
- `PositionId` และ `LevelId` ไม่ถูก Graph/HR flow เปลี่ยน
- profile-only change bump `Version` แต่ไม่ bump `AuthorizationVersion`
- HR/Graph/mapping/mismatch/taken failure rollback JIT/profile/success audits และไม่สร้าง session

## 5. เตรียม `LegacyKey`

migration ไม่ seed mapping Operator ต้องเติม mapping จาก HR source ของ environment เอง ห้าม copy production value ลง
repository

ตรวจ missing mapping แบบ read-only:

```sql
SELECT DISTINCT b.br_code
FROM dbo.branch AS b
WHERE NOT EXISTS (SELECT 1 FROM cfg.Offices AS o WHERE o.LegacyKey = b.br_code);

SELECT DISTINCT e.DepartmentID
FROM dbo.VibEmp AS e
WHERE e.DepartmentID IS NOT NULL AND LTRIM(RTRIM(e.DepartmentID)) <> N''
  AND NOT EXISTS (SELECT 1 FROM cfg.Divisions AS d WHERE d.LegacyKey = e.DepartmentID);
```

template สำหรับ operator-controlled update:

```sql
BEGIN TRAN;
UPDATE cfg.Offices
SET LegacyKey = N'<branch-key>'
WHERE Code = N'<approved-office-code>' AND LegacyKey IS NULL;

UPDATE cfg.Divisions
SET LegacyKey = N'<department-key>'
WHERE Code = N'<approved-division-code>' AND LegacyKey IS NULL;
-- ตรวจว่าแต่ละ statement กระทบหนึ่งแถว ก่อน COMMIT
COMMIT;
```

- filtered unique index บังคับหนึ่ง `LegacyKey` ต่อ mapping row
- mapping ใหม่มีผลกับ login ถัดไปโดยไม่ restart
- Office/Division ที่ Inactive ใช้ได้เฉพาะเมื่อเป็นค่าเดิมของ Admin; การเปลี่ยนไป inactive target ถูกปฏิเสธ
- flow ไม่ใช้ `dbo.branch.active_row`, `dbo.VibEmp.status_code`, `Status` หรือ `TerminatedDate`

ถ้า HR mirror ถูก load หลัง migration ต้อง grant read ด้วย operator process:

```sql
GRANT SELECT ON dbo.VibEmp TO pol_app;
GRANT SELECT ON dbo.branch TO pol_app;
```

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
2. เติม `LegacyKey` และ grant HR reads
3. grant `User.Read` admin consent ถ้าจะเปิด profile switch
4. start new binary ให้ startup tenant/state verifier ผ่าน
5. staging ทดสอบ email-less exact login, JIT, pre-bound invite และ profile switch ทั้ง on/off
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
| `employee-profile-invalid` | profile/HR row malformed หรือ ambiguous | rollback resolution; denied-auth audit |
| `employee-profile-unmapped` | branch/division mapping ไม่พร้อม | rollback resolution; denied-auth audit |

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

ปิด profile switchได้โดยไม่ลบข้อมูล profile และไม่เปลี่ยน identity flow หลัง tenant-aware mapping แล้ว ห้าม deploy
Email-only binary, reconstruct object ID จาก Email หรือรัน guarded identity migration `Down()` ใน production ใช้ forward
recovery หรือ verified backup restore ตาม cutover runbook

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
scripts/spec-trace.sh tier0-microsoft-tenant-aware-identity
```
