# Tier 0 Microsoft Workforce Tenant-aware Identity Cutover

คู่มือนี้เป็นขั้นตอนบังคับสำหรับเปลี่ยน Admin Microsoft จาก historical email identity ไปเป็น immutable
Entra identity โดยไม่เปลี่ยน authorization หรือ session ownership:

```text
Provider = "microsoft"
TenantId = validated tid
Subject  = canonical validated oid
UNIQUE (Provider, TenantId, Subject)
```

Email เป็น contact attribute แบบ optional และ non-unique เท่านั้น อาจไม่มี เปลี่ยน ถูกนำกลับมาใช้ หรือซ้ำกันได้
ระบบไม่ใช้ Email, UPN, `preferred_username`, `WorkforceEmailKey` หรือ `EmployeeId` เพื่อ resolve, bind, recover,
JIT หรืออนุญาต Microsoft login หลัง cutover

## 1. Security boundary ปัจจุบัน

- OIDC Authority ต้อง pin workforce tenant เดียวและลงท้าย `/v2.0`; ห้าม `common`, `organizations` และ
  `consumers`
- Framework ต้องตรวจ authorization-code exchange, signature, issuer, audience, nonce, lifetime และ protocol
  state สำเร็จก่อน workforce validator อ่าน claims
- workforce validator ต้องได้ `tid` และ `oid` อย่างละหนึ่งค่าพอดี เป็น GUID ที่ไม่ว่าง และ `tid` ต้องตรงกับ
  configured tenant
- runtime และ recovery query ใช้ exact `(microsoft, tid, oid)` เท่านั้น
- row ใหม่จาก JIT เป็น `Active + Scoped`, ไม่มี role และไม่มี `MerchantAccess`; claims ไม่เปลี่ยน Tier, role,
  permission หรือ merchant scope
- session และ audit ยังอ้าง internal `AdminId`; external tuple และ optional Email ไม่ถูกใส่ใน session token/cookie
- `WorkforceTenantBinding` ยังเป็น singleton tenant pin ระดับ deployment

Triple index ในฐานข้อมูลเป็น identity shape เท่านั้น ไม่ใช่สิทธิ์รับ tenant เพิ่ม การรับ tenant ที่สองยังถูกห้าม
จนกว่าจะมี design ของ tenant registry/allowlist ที่อนุมัติแยกต่างหาก

## 2. Authoritative Entra export และ approval

สร้าง mapping ใน trusted operator environment จากสองแหล่งที่ตรวจสอบได้:

1. export inventory ฝั่งระบบโดยใช้ internal `AdminId`
2. authoritative Entra export จาก workforce tenant ที่ pin ไว้ โดยใช้ directory object `id` ซึ่งเป็นค่าเดียวกับ
   token claim `oid`
3. ผู้อนุมัติตรวจคู่ `AdminId` กับ Entra object โดยอาศัย HR/change record ที่อนุมัติ ห้าม derive, hash หรือ fabricate
   `oid` จาก Email
4. snapshot ต้องครอบทุก historical Microsoft row และทุก unbound Admin (`Subject IS NULL`) ณ first run พอดี
5. bound non-Microsoft row ไม่อยู่ใน manifest และต้องถูก preserve

Email ไม่อยู่ใน manifest เพราะไม่มี authentication authority ค่า Email เดิมบน Admin จะถูก preserve byte-equivalent
ระหว่าง offline mapping

เก็บ approval record ในระบบหลักฐานที่ immutable และกำหนด correlation reference แบบ non-sensitive สำหรับ audit
ห้ามใส่ `tid`, `oid`, Email, `EmployeeId`, manifest content/path/digest, target หรือ raw approval evidence ลง log,
ticket comment หรือ CI output

## 3. Strict manifest

ไฟล์เป็น UTF-8 JSON object ที่มีเพียง `schemaVersion` และ `entries`; แต่ละ entry มีสาม property เท่านั้น:

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "adminId": "<non-empty-guid>",
      "tenantId": "<non-empty-guid>",
      "objectId": "<non-empty-guid>"
    }
  ]
}
```

ข้อบังคับ:

- ชื่อ property เป็น case-sensitive; unknown, missing หรือ duplicate property ถูกปฏิเสธ
- trailing comma และ JSON comment ถูกปฏิเสธ
- ไฟล์ต้องไม่ว่างและต้องไม่เกิน 10 MiB
- `AdminId` ต้องครอบ first-run snapshot พอดี ไม่มี missing, extra, duplicate หรือ foreign row
- ทุก `tenantId` ต้องตรง `WORKFORCE_TENANT_ID` และ singleton tenant binding
- exact tuple ที่ได้ต้องไม่ซ้ำและต้องไม่ขัดกับ final identity ที่มีอยู่
- tool อ่านไฟล์ครั้งเดียว ตรวจว่า size ไม่เปลี่ยน และเปรียบเทียบ SHA-256 แบบ fixed time ก่อนเริ่ม mapping

สร้าง digest เข้า environment โดยไม่พิมพ์ digest หรือ path:

```bash
manifest_file=./secrets/workforce_identity_manifest.json
WORKFORCE_IDENTITY_MANIFEST_SHA256="$(openssl dgst -sha256 -r < "$manifest_file" | awk '{print $1}')"
export WORKFORCE_IDENTITY_MANIFEST_SHA256
```

ห้าม commit manifest หรือเก็บไว้ใน build context/artifact หลังจบ cutover จำกัด permission ของไฟล์ให้ operator
เท่านั้น

## 4. First-run inputs

first run ที่มี row ต้องได้รับทุกค่าต่อไปนี้ผ่าน protected operator environment:

| Environment variable | Contract |
|---|---|
| `WORKFORCE_IDENTITY_MANIFEST_FILE` | path ภายใน migrate container ไปยัง read-only ephemeral manifest |
| `WORKFORCE_IDENTITY_MANIFEST_SHA256` | SHA-256 64 hex characters ของ bytes จริง |
| `WORKFORCE_IDENTITY_TARGET` | exact `server:port/database`; ต้องตรง connection และ database จริง |
| `WORKFORCE_IDENTITY_APPROVAL_EVIDENCE` | non-empty trimmed approval reference ยาวไม่เกิน 256; validate เท่านั้น ไม่เขียน audit |
| `WORKFORCE_IDENTITY_CORRELATION_ID` | non-sensitive trimmed reference ยาวไม่เกิน 128; ใช้กับ per-row system audit |
| `WORKFORCE_TENANT_ID` | non-empty tenant GUID เดียวกับ Authority และทุก manifest entry |

อย่าพิมพ์ค่าของตัวแปรเหล่านี้ ใช้คำสั่งที่ส่งเฉพาะชื่อ environment variable เข้า container:

```bash
export WORKFORCE_IDENTITY_TARGET WORKFORCE_IDENTITY_APPROVAL_EVIDENCE
export WORKFORCE_IDENTITY_CORRELATION_ID WORKFORCE_TENANT_ID

docker compose -f docker-compose.prod.yml run --rm --no-deps \
  -v "$(pwd)/secrets/workforce_identity_manifest.json:/run/workforce/manifest.json:ro" \
  -e WORKFORCE_IDENTITY_MANIFEST_FILE=/run/workforce/manifest.json \
  -e WORKFORCE_IDENTITY_MANIFEST_SHA256 \
  -e WORKFORCE_IDENTITY_TARGET \
  -e WORKFORCE_IDENTITY_APPROVAL_EVIDENCE \
  -e WORKFORCE_IDENTITY_CORRELATION_ID \
  -e WORKFORCE_TENANT_ID \
  migrate
```

คำสั่งนี้คงลำดับเดิมของ migrate entrypoint: bootstrap principals และ upstream simulations จากนั้น apply committed
idempotent schema script แล้วจึงรัน `WorkforceIdentityMigrator` ห้ามรัน tool ก่อน schema หรือแยก tool ไปใช้ runtime
principal

fresh database ที่ไม่มี Admin row สามารถ complete ด้วย counts ศูนย์โดยไม่ใช้ manifest หลังจากนั้น API startup จะสร้าง
และตรวจ singleton จาก tenant-pinned Authority ตามปกติ completed rerun ต้อง verify ได้โดยไม่ต้องใช้ manifest และต้อง
ไม่ขยาย first-run snapshot ด้วย JIT หรือ pre-bound invite ที่สร้างภายหลัง

## 5. Staging preflight

1. ใช้ isolated staging database หรือ restored staging copy เท่านั้น
2. ยืนยัน Authority, client, callback, proxy และ secret injection โดยไม่พิมพ์ค่า
3. grant `User.Read` และเตรียม HR mapping ก่อนเปิด employee-profile switch ตาม
   [Admin Microsoft OIDC runbook](admin-microsoft-oidc.md)
4. สร้าง manifest จาก synthetic Admin/Entra records และทดสอบ success, digest mismatch, wrong target, missing/extra/
   duplicate entry, tenant mismatch และ rerun
5. ทดสอบ email-less exact login, roleless JIT, pre-bound invite และ employee-profile success/failure
6. ทดสอบ same-tuple concurrency และต่าง tuple ที่ใช้ Email เดียวกัน
7. ยืนยัน failure ทุกชนิดไม่มี partial User/profile/audit/session
8. ลบ manifest copy หลังแต่ละ rehearsal

JIT และ invite mutation smoke ทำใน staging เท่านั้น Production smoke ต้องใช้ approved existing pre-mapped account

## 6. Production cutover

ห้าม mixed-version traffic เพราะ schema ใหม่ลบ `WorkforceEmailKey` และ Email uniqueness ซึ่งไม่ compatible กับ
binary เก่า

1. ผ่าน CI, staging rehearsal และ migration/script/bootstrap gates
2. สร้าง verified backup แล้วบันทึก checksum และ rollback evidence ตาม release process
3. เปิด maintenance window ปิด Admin Microsoft traffic และ drain old API ทุก instance
4. ตรวจ exact target, image digest, authoritative export, manifest digest และ approval โดยไม่ copy ค่าเข้าสู่ log
5. รัน first-run migrate command ในข้อ 4 และ require exit `0`
6. ตรวจ aggregate completion gate ในข้อ 7
7. ลบ ephemeral manifest ทั้ง host copy และ mount; unset first-run variables
8. start เฉพาะ new binary; startup ต้องยืนยัน configured tenant ตรง singleton, old/new migration state completed และ
   Admin ทุก row อยู่ final state
9. login ด้วย operator-approved existing pre-mapped account และยืนยัน session, `/api/v1/admins/me`, RBAC, rotation,
   revocation และ CSRF
10. เปิด traffic และ monitor เฉพาะ aggregate categories เช่น `resolved`, `jit`, `suspended`, `identity-conflict`

Tool output ที่ยอมรับมีเพียง fixed category และ aggregate counts:

```text
[workforce-identity] completed: snapshot=<count> mapped=<count> no-op=<count>
[workforce-identity] verified: snapshot=<count> mapped=<count> no-op=<count>
[workforce-identity] failed: <fixed-category>
```

ห้ามเพิ่ม per-row identity, exception message, manifest metadata หรือ target ใน output

## 7. Aggregate completion gate

ใช้ query ต่อไปนี้จาก privileged operator session ผลลัพธ์ต้องมี state row เดียว, `IsComplete=1`, counts รวมกันถูกต้อง
และ `InvalidFinalRows=0`:

```sql
SELECT SnapshotCount, MappedCount, NoOpCount,
       CASE WHEN CompletedAt IS NULL THEN 0 ELSE 1 END AS IsComplete
FROM admin.WorkforceTenantIdentityMigrations
WHERE Id = 1;

SELECT COUNT_BIG(*) AS InvalidFinalRows
FROM admin.Users
WHERE Subject IS NULL
   OR (Provider COLLATE Latin1_General_100_BIN2 = N'microsoft' AND TenantId IS NULL);
```

query นี้เป็น readiness summary ไม่ใช่ auth lookup Tool และ startup ตรวจ canonical provider/object ID, singleton,
snapshot และ exact tuple invariant เข้มกว่านี้

Offline mapping ต้อง:

- preserve `AdminId`, Email, Status, Tier, roles, permissions, `MerchantAccess`, employee profile,
  `AuthorizationVersion`, sessions และ historical audits
- bump resource `Version` หนึ่งครั้งต่อ mapped row โดยไม่ bump `AuthorizationVersion`
- append action `microsoft-email-bind` เพื่อ compatibility โดย actor เป็น migration/system และ target เป็น internal
  `AdminId`; correlation เป็น non-sensitive reference และไม่มี raw approval evidence

## 8. Pre-bound invite หลัง cutover

`POST /api/v1/admins` เป็น Super + CSRF operation และรับ:

```json
{
  "objectId": "<verified-entra-object-guid>",
  "identityApprovalReference": "<non-sensitive-reference>",
  "email": "<optional-contact>"
}
```

`objectId` ต้องมาจาก verified Entra export ของ tenant ที่ persist ไว้ ระบบสร้าง exact tuple ตั้งแต่ invite และ first login
resolve `AdminId` เดิม ห้ามสร้าง invite ที่รอ bind ด้วย Email และห้ามใช้ Email/EmployeeId เพื่อ recovery

Email ที่ absent/blank/overlength ถูกเก็บเป็น `NULL` โดยไม่ block valid identity invite Email ซ้ำกันได้
`identityApprovalReference` ถูก trim, bounded และเก็บเป็น `create-scoped` audit correlation ใน transaction เดียว

## 9. Failure และ recovery

- schema/tool/target/digest/coverage/invariant failure: คง traffic ปิด แก้ input หรือข้อมูลที่ได้รับอนุมัติ แล้ว rerun
  forward ด้วย binary เดิม ห้าม fallback ไป Email และห้าม fabricate `oid`
- tool ใช้ serializable transaction กับ transaction-owned identity/tenant applocks Failure ต้อง rollback snapshot,
  binding, mappings และ audits ทั้งชุด
- startup failure: ห้าม bypass readiness หรือ start old binary ตรวจ state ด้วย aggregate query และแก้ forward
- ก่อน mapping และก่อนเปิด traffic การ rollback production หลักคือ restore verified backup พร้อม previous compatible tag
- หลัง mapping, JIT หรือ invite มี `TenantId` แล้ว guarded `Down()` ต้อง abort ก่อน DDL ใช้ forward recovery หรือ approved
  backup restore ตาม release decision เท่านั้น
- ห้าม reconstruct external object ID จาก Email เพื่อทำ reverse migration
- ห้ามรัน migration `Down()` ใน production

Existing sessions ไม่ถูก revoke จาก migration Session ownership ยังคง internal `AdminId` และใช้ expiry, rotation,
reuse detection และ revocation policy เดิม

## 10. Blockers ก่อน multi-tenant

ต้องมี approved tenant-registry design แยกต่างหากก่อนรับ manifest หรือ token จาก tenant ที่สอง โดยอย่างน้อยต้องเปลี่ยน:

| Current single-tenant assumption | สิ่งที่ต้องอนุมัติก่อน tenant ที่สอง |
|---|---|
| singleton `WorkforceTenantBinding` + FK | tenant registry/allowlist, onboarding audit และ FK ไป registry |
| tenant-pinned Authority | registry-driven exact issuer/discovery metadata และ fail-closed authority selection |
| optional non-unique Email | privacy/retention policy เท่านั้น ห้ามทำ tenant-scoped secondary identity key |
| ไม่มี `WorkforceEmailKey` | ห้ามสร้าง replacement Email bridge; identity ยังคง `tid + oid` |
| global unique `EmployeeId` | HR-domain review ว่า namespace เป็น global หรือ `(TenantId, EmployeeId)` |
| first-run snapshot เดียว | tenant-aware onboarding migration ownership/versioning |

Database triple index ไม่ใช่ approval ให้เปิด multi-tenant และ runtime ไม่มี tenant-management UI/API ใน cutover นี้

## 11. Verification ก่อน ship

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
