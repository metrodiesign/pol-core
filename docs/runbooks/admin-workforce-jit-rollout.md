# Tier 0 Microsoft Workforce Canonical Email Rollout

คู่มือนี้ใช้ deploy `Tier 0 : Microsoft Azure ID (สำหรับพนักงาน)` สำหรับ Admin ของ `pol-core`.
ระบบใช้ canonical corporate email เป็น Microsoft subject และไม่ใช้ Microsoft Graph.

## Contract ปัจจุบัน

- รับเฉพาะ issuer จาก tenant-pinned Authority และ claim `tid` หนึ่งค่าที่ตรง workforce tenant.
- เลือก `email` เมื่อมีหนึ่งค่า. ใช้ `preferred_username` เฉพาะเมื่อไม่มี `email`.
- ถ้า `email` มีแต่ใช้ไม่ได้ ระบบปฏิเสธและไม่ fallback.
- canonicalizer trim ขอบ, รับเฉพาะ ASCII addr-spec ยาวไม่เกิน 254 ตัวอักษร, ใช้ BCL
  `MailAddress`, reject display name/whitespace ภายใน และ lowercase แบบ invariant.
- domain ต้องเท่ากับ `viriyah.co.th`; subdomain และ domain ภายนอกไม่ผ่าน.
- identity key คือ `(provider=microsoft, subject=<canonical-email>)`.
- Tier 0 ไม่อ่านหรือบังคับ `roles` และไม่ใช้ `oid` ใน runtime lookup/persistence.
- Active Admin ที่ canonical email ตรงและยัง unbound ถูก bind เข้าบัญชีเดิม โดยคง tier, roles,
  permissions และ MerchantAccess.
- Suspended, bound-other, duplicate owner หรือ divergent identity fail closed.
- email ใหม่ที่ไม่ match สร้าง `Active + Scoped` แบบไม่มี role และ MerchantAccess.
- route `PUT /api/v1/admins/{id}/microsoft-identity` ถูก retire และตอบ normal `404`.

Admin Google login/callback ยังไม่ register. Merchant Google/Microsoft flow ไม่เปลี่ยน.

## Staging preflight

### Entra application

1. ตั้ง Web redirect URI เป็น
   `https://<api-origin>/api/v1/admins/auth/microsoft/callback` แบบ exact.
2. ตั้ง Authority เป็น
   `https://login.microsoftonline.com/<workforce-tenant-id>/v2.0`.
3. เก็บ client secret `Value` ใน secret store; ห้าม commit หรือพิมพ์ใน log.
4. ใช้ Conditional Access, MFA และ Enterprise Application assignment เป็น access policy ฝั่ง Entra.
5. ไม่ต้องสร้าง App Role สำหรับ Tier 0 contract นี้.
6. ตรวจว่า production เปิด Admin Microsoft provider เดียวและไม่เปิด Admin Google.

### Automated gates

```bash
dotnet build pol-core.slnx --no-restore -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test pol-core.slnx --filter "Category=Integration"
bash docker/migrate-entrypoint.test.sh
scripts/spec-trace.sh tier-0-microsoft-canonical-email
```

Staging smoke ต้องยืนยันว่า missing/empty/unrelated role claim และ missing/malformed `oid` ไม่ทำให้ถูกปฏิเสธ;
พร้อมตรวจ email precedence, wrong tenant, malformed email, wrong domain, existing bind, Suspended และ roleless JIT.

## Production cutover

ห้ามให้ binary แบบ legacy subject และ binary แบบ canonical-email รับ Tier 0 traffic พร้อมกัน.

1. ผ่าน staging และบันทึก image digest/test evidence.
2. สร้าง verified database backup พร้อม artifact URI และ SHA-256 checksum.
3. เปิด maintenance window, ปิด Tier 0 traffic และ drain API instance เก่าทั้งหมด.
4. รัน migrate image. ลำดับบังคับคือ EF migration แล้ว
   `WorkforceIdentityMigrator` ด้วย privileged `POL_DESIGN_SQL`.
5. Require migrate service exit `0`. Tool ต้องรายงาน completed/verified counts โดยไม่มี identity value.
6. ตรวจ `admin.WorkforceIdentityMigrations.CompletedAt` ไม่เป็น `NULL` และ counts ตรง manifest.
7. Start เฉพาะ binary ใหม่. Startup gate ต้องผ่านก่อน readiness.
8. รัน approved synthetic login: existing binding และ roleless JIT.
9. เปิด traffic และแนบ timestamp, health, migration counts และ smoke evidence ใน release record.

`WorkforceIdentityMigrator` ทำงานใน serializable transaction ภายใต้
`admin-user-identity-mutation` applock. Invalid email, duplicate canonical owner, unknown subject หรือ drift
ทำให้ conversion ทั้งชุด rollback และ migrate service exit non-zero.

## Verification หลัง cutover

- Existing Admin ID, status, tier, authorization/resource version, role assignments และ MerchantAccess ไม่เปลี่ยน.
- UUID Microsoft subject เปลี่ยนเป็น canonical email ของ row เดิม.
- Microsoft subject ที่เป็น canonical email ตรงอยู่แล้วเป็น no-op.
- ทุก Admin มี `WorkforceEmailKey` ตรง canonicalizer หรือ `NULL` สำหรับ email นอก workforce contract.
- `microsoft-email-bind` และ `jit-provision` audit ใช้เฉพาะ internal Admin ID; ไม่มี email หรือ legacy subject.
- `/api/v1/admins/me`, RBAC, session rotation/revocation และ CSRF ยังทำงานเดิม.
- retired pre-provision route ตอบ `404` และไม่เกิด identity mutation.

## Rollback boundary

### ก่อนเปิด Tier 0 traffic

หยุด rollout. ใช้ verified backup restore เป็น production rollback หลัก แล้ว deploy prior binary.
Migration `Down` มี guard และ restore legacy subjects จาก rollback manifest สำหรับ non-production proof;
ถ้า row set, manifest หรือ current subject drift มันต้อง abort ก่อน DDL.

ตรวจ health/readiness และ legacy authentication ก่อนเปิด trafficกลับ. เก็บ backup/checksum และ rollback
evidence ไว้กับ release.

### หลังเปิด Tier 0 traffic

ห้าม restore legacy subjectsหรือ deploy binary ที่รู้จักเฉพาะ legacy subject. หลังเปิด trafficอาจมี canonical
JIT identity, binding และ audit ใหม่แล้ว จึงต้องใช้ forward recovery เท่านั้น.

## Email rename และ reuse risk

Corporate email เปลี่ยนและนำกลับมาใช้ซ้ำได้ จึงพิสูจน์ continuity ของบุคคลได้น้อยกว่า immutable directory ID.

- Rename ไม่ transfer authorization. Email ใหม่ที่ไม่ match จะได้ roleless Scoped JIT account.
- Reuse อาจชี้ account เดิม. Lifecycle owner ต้อง suspend Admin ของเจ้าของเดิมและ revoke sessions
  ก่อนองค์กร assign email เดิมให้บุคคลใหม่.
- ห้าม rebind หรือ overwrite identity เพื่อแก้ collision. ใช้ approved forward-recovery procedure.
- ตรวจ orphan/renamed/reused email ตามรอบ access review และเก็บ internal Admin ID เป็น audit anchor.

## Troubleshooting

| อาการ | ตรวจ |
|---|---|
| `workforce-access-denied` | issuer/tenant, claim precedence, canonical format และ exact domain |
| `identity-conflict` | duplicate email owner, bound-other หรือ identity/email divergence |
| API ไม่ ready หลัง migration | completed state, `WorkforceEmailKey` และ Microsoft subject invariant |
| migrate service exit non-zero | หยุด rollout; inspect fixed failure category และตรวจ data แบบ privileged โดยไม่ copy identity ลง ticket |
| retired route `404` | expected behavior; ไม่มี replacement endpoint |

ห้ามแนบ authorization code, ID/access token, cookie, session token, canonical email, legacy subject หรือ
connection string ใน log, ticket หรือ rollout evidence.
